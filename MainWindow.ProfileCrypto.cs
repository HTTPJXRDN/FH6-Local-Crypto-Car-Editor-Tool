using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    private string? _templateProfile;
    private bool StageProfileInput(string path) {
        if(!ProfileCrypto.IsProfilePath(path))return false;
        bool database=ProfileCrypto.IsDatabaseInput(path);
        bool plain=database||ProfileCrypto.IsPlainPayload(path);
        ProfileTemplateRow.Visibility=Visibility.Visible;
        DbTemplateRow.Visibility=Visibility.Collapsed;
        StagedText.Text=$"Staged: {Path.GetFileName(path)}   (FH6 C_ProfileData → {(plain?"Re-encrypt":"Decrypt")})";
        StagedText.Visibility=Visibility.Visible;
        Log(database?"FH6 profile SQLite staged. Re-encrypt puts the edited database into the original C_ProfileData template, retaining its other three sections. This is not a GameDB."
            :plain?"Decrypted full FH6 profile staged. Legacy full-payload re-encryption is still supported with an original C_ProfileData template."
            :"FH6 C_ProfileData staged. Decrypt exports its embedded database as a standalone .sqlite without changing the original.");
        Status("FH6 profile staged.");return true;
    }
    private async Task DecryptProfileFlow(string path) {
        Status($"Decrypting FH6 profile {Path.GetFileName(path)}…");
        string output=UniqueOutputPath(Path.Combine(OutputDirFor(path),Path.GetFileName(path)+".sqlite"));
        await Task.Run(()=>ProfileCrypto.DecryptDatabase(path,output));
        _templateProfile=path;ProfileTemplateText.Text=path;
        Log($"Exported FH6 profile SQLite: {Path.GetFileName(output)}. Container and SQLite validated; original unchanged.");
        Log("Edit the .sqlite in DB Browser or an external SQLite editor, write/export changes, then drop it here and Re-encrypt. The original profile template preserves the other three save sections. Profile Editor / Garage Viewer continue to load complete profiles.");
        Done(output,"Profile SQLite exported OK.");
    }
    private async Task EncryptProfileFlow(string path) {
        string? template=_templateProfile;
        if(template==null||!File.Exists(template)) {
            Log("Re-encrypt needs the original encrypted FH6 C_ProfileData. Decrypt it first or use the separate Profile template Browse button.");
            Status("No profile template set; no output written.");return;
        }
        Status($"Re-encrypting FH6 profile {Path.GetFileName(path)}…");
        bool database=ProfileCrypto.IsDatabaseInput(path);
        string stem=Path.GetFileName(path);
        if(database)stem=Path.GetFileNameWithoutExtension(stem);
        else if(stem.EndsWith(".decrypted",StringComparison.OrdinalIgnoreCase))stem=stem[..^10];
        string output=UniqueOutputPath(Path.Combine(OutputDirFor(path),stem+".re-encrypted"));
        await Task.Run(()=>{if(database)ProfileCrypto.EncryptDatabase(path,template,output);else ProfileCrypto.Encrypt(path,template,output);});
        if(database)Log("Edited SQLite reinserted; original profile, save-state and binary career sections preserved byte-for-byte from the selected template.");
        Log($"Re-encrypted FH6 profile: {Path.GetFileName(output)}. Authentication and exact decrypted-payload verification passed; originals unchanged.");
        Log("Back up the entire save and close the game before installing. Rename the exported copy to C_ProfileData only when ready.");
        Done(output,"Profile re-encrypted and verified.");
    }
    private async void BrowseProfileTemplate_Click(object sender,RoutedEventArgs e) {
        if(_cryptoBusy)return;
        string? path=BrowseProfile("Select the original encrypted FH6 C_ProfileData template");if(path==null)return;
        try {SetCryptoBusy(true);await Task.Run(()=>ProfileCrypto.ValidateTemplate(path));_templateProfile=path;ProfileTemplateText.Text=path;Log("Validated FH6 profile template set: "+Path.GetFileName(path));Status("Profile template set.");}
        catch(Exception ex) {Fail(ex,path);}finally {SetCryptoBusy(false);}
    }
}
