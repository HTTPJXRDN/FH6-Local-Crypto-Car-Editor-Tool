#include <cstdint>
#include <cstring>

#include "acl/core/ansi_allocator.h"
#include "acl/core/compressed_tracks.h"
#include "acl/core/impl/debug_track_writer.h"
#include "acl/decompression/decompress.h"

#include "rtm/quatf.h"
#include "rtm/vector4f.h"

extern "C" __declspec(dllexport) int acl_decompress_pose_samples(
    const uint8_t* compressed_data,
    int compressed_data_size,
    const uint32_t* /*bone_hashes*/,
    int bone_count,
    int sample_count,
    float duration,
    float* output,
    int output_float_count)
{
    if (compressed_data == nullptr || output == nullptr || compressed_data_size < 32 ||
        bone_count <= 0 || sample_count <= 0)
        return -1;

    const auto* tracks = reinterpret_cast<const acl::compressed_tracks*>(compressed_data);
    if (tracks->get_size() != static_cast<uint32_t>(compressed_data_size))
        return -2;
    if (!tracks->is_valid(false).empty())
        return -3;
    if (tracks->get_track_type() != acl::track_type8::qvvf)
        return -4;
    if (tracks->get_num_tracks() != static_cast<uint32_t>(bone_count))
        return -5;

    const int required_float_count = bone_count * sample_count * 10;
    if (output_float_count < required_float_count)
        return -6;

    acl::decompression_context<acl::debug_transform_decompression_settings> context;
    if (!context.initialize(*tracks))
        return -7;

    acl::ansi_allocator allocator;
    acl::acl_impl::debug_track_writer_constant_defaults writer(
        allocator, acl::track_type8::qvvf, static_cast<uint32_t>(bone_count));

    for (int sample_index = 0; sample_index < sample_count; ++sample_index)
    {
        const float sample_time = sample_count > 1
            ? duration * static_cast<float>(sample_index) / static_cast<float>(sample_count - 1)
            : 0.0F;

        context.seek(sample_time, acl::sample_rounding_policy::nearest);
        context.decompress_tracks(writer);

        for (int track_index = 0; track_index < bone_count; ++track_index)
        {
            const rtm::qvvf& transform = writer.read_qvv(static_cast<uint32_t>(track_index));
            float* destination = output + ((sample_index * bone_count + track_index) * 10);
            rtm::vector_store3(transform.translation, destination + 0);
            rtm::quat_store(transform.rotation, destination + 3);
            rtm::vector_store3(transform.scale, destination + 7);
        }
    }

    return required_float_count;
}

