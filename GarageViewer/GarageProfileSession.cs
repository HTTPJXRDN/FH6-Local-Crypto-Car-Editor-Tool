using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using static FH6LocalCryptoTool.GarageViewer.GarageCarCatalog;

namespace FH6LocalCryptoTool.GarageViewer;

public sealed record GarageEntry(long Id, long CarId, string Guid, bool Favorite, double PerformanceIndex, long ClassId, string Protection)
{
    public string ThumbnailReference { get; init; } = "";
    public bool IsProtected => Protection.Length != 0;
}

/// <summary>Garage-only edits on a private SQLite copy. Account/progress sections remain byte-exact.</summary>
public sealed class GarageProfileSession : IDisposable
{
    readonly string _work;
    readonly SqliteConnection _db;
    readonly byte[] _original, _stream;
    readonly ProfileContainer _profile;
    readonly bool _encrypted;
    readonly HashSet<long> _protected = new();
    readonly HashSet<long> _pending = new();
    readonly HashSet<string> _barnVins = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _garageColumns;
    readonly HashSet<string> _tables;
    readonly bool _carDetailsLayout;
    long _nextId;
    long _originalCurrentId;
    public string SourcePath { get; }
    public string SourceHash { get; }
    public bool HasChanges { get; private set; }
    public string EditRestriction { get; private set; } = "";
    public bool CanEdit => EditRestriction.Length == 0;
    public bool CanEditCarDetails => CanEdit && _carDetailsLayout;
    public long CurrentGarageId { get; private set; }
    public bool CanChangeCurrentCar => CanEdit && _profile.Properties?.Walk().Any(p => p.Path == "/Main/CareerCar" && p.Node.RawValue.Length == 4 && p.Node.TypeId is 3 or 7 or 17) == true;
    public long Count => L(Scalar("SELECT COUNT(*) FROM Career_Garage"));

    GarageProfileSession(string source)
    {
        SourcePath = Path.GetFullPath(source); _original = File.ReadAllBytes(SourcePath);
        SourceHash = Convert.ToHexString(SHA256.HashData(_original));
        _encrypted = _original.Length < 4 || !_original.AsSpan(0,4).SequenceEqual(new byte[]{0xB6,0xF2,0x8B,0x4A});
        _stream = _encrypted ? ProfileData.Decrypt(_original) : _original;
        _profile = ProfileContainer.Parse(_stream);
        _work = Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, $"forza_garage_profile_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
        File.WriteAllBytes(_work, _profile.Database);
        _db = Open(_work, SqliteOpenMode.ReadWrite);
        try {
            if (Convert.ToString(Scalar("PRAGMA integrity_check")) != "ok") throw new InvalidDataException("Profile database failed integrity_check.");
            _tables = Read("SELECT name FROM sqlite_master WHERE type='table'").Select(r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!_tables.Contains("Career_Garage")) throw new InvalidDataException("This profile has no Career_Garage table.");
            var schema = Read("PRAGMA table_info(Career_Garage)");
            _garageColumns = schema.Select(c => (string)c["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _carDetailsLayout = new[]{("OriginalOwner","TEXT"),("DistanceDriven","INT"),("TopSpeed","REAL")}
                .All(field=>schema.Any(c=>string.Equals((string)c["name"],field.Item1,StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string)c["type"],field.Item2,StringComparison.OrdinalIgnoreCase)));
            if (!new[]{"Id","CarId","Guid"}.All(_garageColumns.Contains)) throw new InvalidDataException("Garage layout is not recognized.");
            if (schema.Count(c => L(c["pk"]) != 0) != 1 || !schema.Any(c => (string)c["name"] == "Id" && L(c["pk"]) == 1 && (string)c["type"] == "INTEGER"))
                EditRestriction = "This garage has an unsupported identity schema; viewing only.";
            if (!_tables.Contains("Career_PurchasedParts") || !new[]{"GarageId","UngroupedPartEnum","PartId","PricePaid"}.All(c => Columns("Career_PurchasedParts").Contains(c)))
                EditRestriction = "Purchased-parts layout is not recognized; viewing only.";
            foreach (string table in _tables.Where(t => t != "Career_Garage" && t != "Career_PurchasedParts"))
                if (Columns(table).Contains("GarageId")) EditRestriction = $"Additional garage-linked table {table} is not supported; viewing only.";
            if (_tables.Contains("BarnFinds") && Columns("BarnFinds").Contains("VIN"))
                foreach (var row in Read("SELECT VIN FROM BarnFinds WHERE VIN IS NOT NULL")) _barnVins.Add(Convert.ToString(row["VIN"])!);
            var properties = _profile.Properties;
            var current = properties?.Walk().Where(p => p.Path == "/Main/CareerCar").Select(p => p.Node).SingleOrDefault();
            if (current == null || !long.TryParse(current.DisplayValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                EditRestriction = "The current-car reference could not be verified; viewing only.";
            else {
                _originalCurrentId = CurrentGarageId = id; _protected.Add(id);
                if (L(Scalar("SELECT COUNT(*) FROM Career_Garage WHERE Id=$p0", id)) != 1)
                    EditRestriction = "The current-car reference does not identify a garage record; viewing only.";
            }
            foreach (var p in properties?.Walk().Where(p => p.Path == "/Main/CareerCarNext") ?? Enumerable.Empty<(string Path, PropertyTree.PropertyNode Node)>())
                if (long.TryParse(p.Node.DisplayValue(), out long pending) && pending > 0) { _pending.Add(pending); _protected.Add(pending); }
            _nextId = Math.Max(L(Scalar("SELECT COALESCE(MAX(Id),0) FROM Career_Garage")), _protected.DefaultIfEmpty().Max());
            if (_tables.Contains("Career_PurchasedParts")) _nextId = Math.Max(_nextId, L(Scalar("SELECT COALESCE(MAX(GarageId),0) FROM Career_PurchasedParts")));
            _nextId++;
        } catch { _db.Dispose(); Cleanup(_work); throw; }
    }
    public static GarageProfileSession Load(string source) => new(source);
    HashSet<string> Columns(string table) => Read("PRAGMA table_info("+Q(table)+")").Select(c=>(string)c["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    object Scalar(string sql, params object[] values) { using var c = Command(sql, values); return c.ExecuteScalar() ?? DBNull.Value; }
    SqliteCommand Command(string sql, params object[] values) {
        var c = _db.CreateCommand(); c.CommandText = sql;
        for (int i=0;i<values.Length;i++) c.Parameters.AddWithValue("$p"+i, values[i]);
        return c;
    }
    List<Dictionary<string, object>> Read(string sql, params object[] values) {
        using var c=Command(sql,values); using var r=c.ExecuteReader(); var rows=new List<Dictionary<string,object>>();
        while(r.Read()) rows.Add(Enumerable.Range(0,r.FieldCount).ToDictionary(r.GetName,r.GetValue,StringComparer.OrdinalIgnoreCase));
        return rows;
    }
    void Exec(string sql, params object[] values) { using var c=Command(sql,values); c.ExecuteNonQuery(); }
    string Protection(Dictionary<string, object> row) {
        if (_protected.Contains(L(row["Id"]))) return L(row["Id"]) == CurrentGarageId ? "Current car" : "Pending car reference";
        if (_barnVins.Contains(Convert.ToString(row["Guid"])!))
            return "Barn-find VIN reference";
        return "";
    }
    public IReadOnlyList<GarageEntry> Entries() => Read("SELECT " + string.Join(",", new[]{"Id","CarId","Guid","IsFavorite","PerformanceIndex","ClassID","Thumbnail"}
        .Select(c => _garageColumns.Contains(c) ? Q(c) : "NULL AS " + Q(c))) + " FROM Career_Garage ORDER BY Id")
        .Select(r => new GarageEntry(L(r["Id"]), L(r["CarId"]), Convert.ToString(r["Guid"])!, L(r.GetValueOrDefault("IsFavorite",0L)) != 0,
            r.GetValueOrDefault("PerformanceIndex") is null or DBNull ? 0 : Convert.ToDouble(r["PerformanceIndex"],CultureInfo.InvariantCulture),
            L(r.GetValueOrDefault("ClassID",0L)), Protection(r)) { ThumbnailReference = Convert.ToString(r.GetValueOrDefault("Thumbnail")) ?? "" }).ToArray();
    public IReadOnlyDictionary<string,object> CarDetails(long garageId) {
        if(!new[]{"OriginalOwner","DistanceDriven","TopSpeed"}.All(_garageColumns.Contains))throw new InvalidOperationException("Car detail fields are missing in this profile layout.");
        return Read("SELECT OriginalOwner,DistanceDriven,TopSpeed FROM Career_Garage WHERE Id=$p0",garageId).SingleOrDefault()
            ?? throw new InvalidOperationException("That garage entry no longer exists.");
    }
    public void UpdateCarDetails(long garageId, IReadOnlyDictionary<string,string> fields) => UpdateCarDetails(new[]{garageId},fields);
    public int UpdateCarDetails(IReadOnlyCollection<long> garageIds,IReadOnlyDictionary<string,string> fields) {
        CheckEditable();if(!CanEditCarDetails)throw new InvalidOperationException("Car detail editing is unavailable for this layout.");
        if(garageIds.Count==0)throw new InvalidOperationException("Select at least one garage car.");
        var rows=garageIds.Distinct().Select(Row).ToArray();var values=new Dictionary<string,object>();
        foreach(var field in fields) {
            object value;
            switch(field.Key) {
                case "OriginalOwner":
                    if(field.Value.Length>256||field.Value.Any(char.IsControl))throw new FormatException("Original Owner must be single-line text, at most 256 characters. It does not change your profile/account identity.");
                    value=field.Value;break;
                case "DistanceDriven":
                    if(!int.TryParse(field.Value.Trim(),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int distance)||distance<0)
                        throw new FormatException("Distance Driven must be a whole number from 0 to 2147483647 in stored DB units.");
                    value=(long)distance;break;
                case "TopSpeed":
                    if(!NumericText.TryParseDouble(field.Value,out double speed)||speed<0||speed>float.MaxValue)
                        throw new FormatException("Top Speed must be a finite non-negative decimal in stored DB units; dot or comma is accepted.");
                    value=speed;break;
                default: throw new InvalidOperationException("Only OriginalOwner, DistanceDriven and TopSpeed can be edited here.");
            }
            values[field.Key]=value;
        }
        var updates=rows.Select(row=>(Row:row,Changes:values.Where(field=>!Equals(row[field.Key],field.Value)).ToDictionary(field=>field.Key,field=>field.Value)))
            .Where(update=>update.Changes.Count!=0).ToArray();
        if(updates.Length==0)return 0;
        Atomic(()=>{
            foreach(var update in updates) {
                var changes=update.Changes;var row=update.Row;
                using var c=Command("UPDATE Career_Garage SET "+string.Join(",",changes.Keys.Select((key,i)=>Q(key)+"=$p"+i))+" WHERE Id=$p"+changes.Count+" AND Guid=$p"+(changes.Count+1),changes.Values.Concat(new object[]{row["Id"],row["Guid"]}).ToArray());
                if(c.ExecuteNonQuery()!=1)throw new InvalidOperationException("Garage identity changed; no car fields updated.");
            }
        });
        return updates.Length;
    }
    public void SetCurrentCar(long garageId) {
        CheckEditable();
        if (!CanChangeCurrentCar) throw new InvalidOperationException("This current-car property layout cannot be edited safely.");
        _ = Row(garageId);
        if (garageId <= 0 || garageId > int.MaxValue) throw new InvalidDataException("Garage ID does not fit the current-car reference.");
        if (garageId == CurrentGarageId) return;
        CurrentGarageId = garageId;
        _protected.Clear(); _protected.UnionWith(_pending); _protected.Add(garageId);
        HasChanges = true;
    }
    void CheckEditable() { if (!CanEdit) throw new InvalidOperationException(EditRestriction); }
    void Atomic(Action action) {
        CheckEditable(); long nextId = _nextId; using var tx=_db.BeginTransaction();
        try { action(); tx.Commit(); HasChanges=true; } catch { tx.Rollback(); _nextId = nextId; throw; }
    }
    Dictionary<string, object> Row(long id) => Read("SELECT * FROM Career_Garage WHERE Id=$p0",id).SingleOrDefault()
        ?? throw new InvalidOperationException("That garage entry no longer exists; refresh the list.");
    public int PurchasedPartCount(long garageId) => _tables.Contains("Career_PurchasedParts") ? checked((int)L(Scalar("SELECT COUNT(*) FROM Career_PurchasedParts WHERE GarageId=$p0",garageId))) : 0;
    public void Remove(IReadOnlyCollection<long> ids, IReadOnlySet<long>? lockedModels = null) {
        CheckEditable(); var selected=ids.Distinct().Select(Row).ToArray();
        if(selected.Length==0) throw new InvalidOperationException("Select a garage entry first.");
        if(selected.Length>=Count) throw new InvalidOperationException("Keep at least one car in the garage.");
        foreach(var row in selected) {
            string reason=Protection(row);
            if(reason.Length!=0)throw new InvalidOperationException($"Garage entry {L(row["Id"])} is protected ({reason}). Select a different car in-game before removing it.");
            if(lockedModels?.Contains(L(row["CarId"]))==true)throw new InvalidOperationException("The GameDB marks this model as non-removable.");
        }
        Atomic(()=>{
            foreach(var row in selected){
                Exec("DELETE FROM Career_PurchasedParts WHERE GarageId=$p0",row["Id"]);
                using var c=Command("DELETE FROM Career_Garage WHERE Id=$p0 AND Guid=$p1",row["Id"],row["Guid"]);
                if(c.ExecuteNonQuery()!=1)throw new InvalidOperationException("Garage identity changed; no entries removed.");
            }
        });
    }
    static readonly string[] History = { "PartsValue","DistanceDriven","TimeDriven","TotalWinnings","TotalRepairs","NumVictories","NumPodiums","NumRaces","NumTimesSold","TimeDrivenInRoadTrips","CurOwnerNumRaces","CurOwnerWinnings","NumSkillPointsEarned","HighestSkillScore","SharedID","IsFavorite","HasCurrentOwnerViewedCar" };
    long Insert(Dictionary<string,object> row) {
        if(_nextId>int.MaxValue)throw new InvalidOperationException("Garage entry ID range exhausted.");
        row["Id"]=_nextId; row["Guid"]=System.Guid.NewGuid().ToString();
        string[] columns=row.Keys.Where(_garageColumns.Contains).ToArray();
        Exec("INSERT INTO Career_Garage ("+string.Join(",",columns.Select(Q))+") VALUES ("+string.Join(",",Enumerable.Range(0,columns.Length).Select(i=>"$p"+i))+")",columns.Select(c=>row[c]).ToArray());
        return _nextId++;
    }
    public long Duplicate(long garageId) {
        var row=Row(garageId); long added=0;
        foreach(string field in History.Where(f=>f!="PartsValue"))if(row.ContainsKey(field))row[field]=0L;
        Atomic(()=>{
            added=Insert(row);
            // Copy only this instance's owned parts. Tunes/liveries are referenced,
            // never deleted or reassigned; a new VIN prevents perk/identity reuse.
            Exec("INSERT INTO Career_PurchasedParts (GarageId,UngroupedPartEnum,PartId,PricePaid) SELECT $p0,UngroupedPartEnum,PartId,PricePaid FROM Career_PurchasedParts WHERE GarageId=$p1",added,garageId);
        });return added;
    }
    public long AddStock(long carId, GarageCarCatalog catalog) {
        return AddStocks(new[] { carId }, catalog)[0];
    }
    public IReadOnlyList<long> AddStocks(IReadOnlyCollection<long> carIds, GarageCarCatalog catalog) {
        CheckEditable();
        var ids=carIds.Distinct().ToArray();
        if(ids.Length==0)throw new InvalidOperationException("Select one or more stock cars first.");
        // Resolve every configuration before writing. One transaction means a bad
        // model or failed insert cannot leave a partially added manufacturer/batch.
        var rows=ids.Select(id=>CreateStockRow(id,catalog)).ToArray();
        var added=new List<long>();Atomic(()=>{foreach(var row in rows)added.Add(Insert(row));});return added;
    }
    Dictionary<string,object> CreateStockRow(long carId, GarageCarCatalog catalog) {
        var configuration=catalog.StockConfiguration(carId);
        var row=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
        foreach(var col in Read("PRAGMA table_info(Career_Garage)")){
            string name=(string)col["name"],type=Convert.ToString(col["type"])!;
            if(name=="Id")continue;
            if(name.StartsWith("Tuning_",StringComparison.Ordinal))row[name]=-1d;
            else if(configuration.TryGetValue(name,out var value))row[name]=value;
            else if(History.Contains(name))row[name]=0L;
            else if(name is "NumOwners")row[name]=1L;
            else if(name is "Flags")row[name]=11L; // observed stock/untuned entries
            else if(name is "DefaultManufacturerColorIndex")row[name]=0L;
            else if(name is "VersionedTuneId" or "VersionedLiveryId")row[name]=System.Guid.Empty.ToString();
            else if(name is "VersionedTuneXUID")row[name]=0L;
            else if(name is "TuneFileName" or "LiveryFileName" or "OriginalOwner")row[name]="";
            else if(name is "Guid")row[name]=System.Guid.NewGuid().ToString();
            else if(name is "FrontTireAspectRatioOffset" or "RearTireAspectRatioOffset")row[name]=-1d;
            else if(L(col["notnull"])==0)row[name]=DBNull.Value;
            else if(col["dflt_value"] is DBNull)throw new InvalidDataException("Unknown required garage field "+name+"; adding stock cars is disabled for this layout.");
        }
        return row;
    }
    public void Export(string destination) {
        destination=Path.GetFullPath(destination);
        if(destination.Equals(SourcePath,StringComparison.OrdinalIgnoreCase)||File.Exists(destination))throw new IOException("Choose a new output filename; input and existing files are never overwritten.");
        if(Convert.ToString(Scalar("PRAGMA integrity_check"))!="ok")throw new InvalidDataException("Garage database failed integrity_check; no save exported.");
        string snapshot=_work+".export.sqlite";
        string candidate=Path.Combine(Path.GetDirectoryName(destination)!,".garage-export-"+System.Guid.NewGuid().ToString("N")+".tmp");
        try {
            Cleanup(snapshot);using(var output=Open(snapshot,SqliteOpenMode.ReadWriteCreate))_db.BackupDatabase(output);
            var exported=ProfileContainer.Parse(_stream);exported.Database=File.ReadAllBytes(snapshot);
            if(CurrentGarageId != _originalCurrentId) {
                var current=exported.Properties?.Walk().Single(p=>p.Path=="/Main/CareerCar").Node;
                if(current==null || !current.TrySetFromText(CurrentGarageId.ToString(CultureInfo.InvariantCulture)))
                    throw new InvalidDataException("The current-car reference could not be serialized; no save exported.");
            }
            byte[] stream=exported.Serialize();byte[] bytes=_encrypted?ProfileData.Encrypt(stream,_original):stream;
            byte[] check=_encrypted?ProfileData.Decrypt(bytes):bytes;
            if(!stream.AsSpan().SequenceEqual(check))throw new InvalidDataException("Exported save failed payload round-trip verification.");
            var packed=ProfileContainer.Parse(check);
            // Restore only the explicitly changed current-car leaf on a verification
            // copy. Exact equality then proves every other account/progress byte stayed intact.
            if(CurrentGarageId != _originalCurrentId) {
                var current=packed.Properties?.Walk().Single(p=>p.Path=="/Main/CareerCar").Node;
                if(current==null || current.DisplayValue()!=CurrentGarageId.ToString(CultureInfo.InvariantCulture) ||
                    !current.TrySetFromText(_originalCurrentId.ToString(CultureInfo.InvariantCulture)))
                    throw new InvalidDataException("Exported current-car reference failed verification.");
                packed = ProfileContainer.Parse(packed.Serialize());
            }
            for(int i=0;i<3;i++)if(!packed.Sections[i].Payload.AsSpan().SequenceEqual(_profile.Sections[i].Payload))throw new InvalidDataException("An unexpected account/progress change occurred; export rejected.");
            using (var file=new FileStream(candidate,FileMode.CreateNew,FileAccess.Write)) { file.Write(bytes);file.Flush(true); }
            File.Move(candidate,destination,false);HasChanges=false;
        } finally { Cleanup(snapshot); Cleanup(candidate); }
    }
    public void Revert() {
        string snapshot=_work+".revert.sqlite";
        try {
            File.WriteAllBytes(snapshot,_profile.Database);
            using var original=Open(snapshot,SqliteOpenMode.ReadOnly);
            original.BackupDatabase(_db);
            CurrentGarageId=_originalCurrentId;
            _protected.Clear();_protected.UnionWith(_pending);_protected.Add(CurrentGarageId);
            HasChanges=false;
        } finally { Cleanup(snapshot); }
    }
    public void Dispose(){_db.Dispose();Cleanup(_work);}
}
