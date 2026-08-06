#pragma once

#include <array>
#include <cstdint>

namespace refii::renderer {

struct BinkVideoPlaneBinding {
  uint32_t movie = 0;
  uint32_t surface = 0;
  uint32_t plane_info = 0;
  uint32_t plane_output_slot = 0;
  uint32_t physical_address = 0;
  uint32_t row_pitch = 0;
  uint32_t upload_size = 0;
  uint32_t decoded_plane_index = UINT32_MAX;
  uint32_t decoded_address = 0;
  uint32_t decoded_row_pitch = 0;
  uint32_t decoded_frame_table = 0;
  uint32_t decoded_frame_index = UINT32_MAX;
  bool decoded_current_frame_target = false;
  uint64_t serial = 0;
};

struct RendererBinkVideoPlaneSnapshot {
  std::array<uint32_t, 3> surfaces = {};
  std::array<uint64_t, 3> serials = {};
  uint32_t movie = 0;
  uint32_t decoded_frame_table = 0;
  uint32_t decoded_frame_index = UINT32_MAX;
  bool complete = false;
};

struct BinkVideoPlaneSetupProbe {
  uint32_t selected_address = 0;
  int32_t first_nonzero = -1;
};

BinkVideoPlaneBinding MakeBinkVideoPlaneBinding(
    uint32_t movie,
    uint32_t surface,
    uint32_t plane_info,
    uint32_t plane_output_slot,
    uint32_t physical_address,
    uint32_t row_pitch,
    uint32_t upload_size,
    uint64_t serial);

namespace bink {

using BinkDecodeGpuPointer = uint32_t (*)(uint32_t pointer);
using BinkSelectReadableUploadAddress = uint32_t (*)(uint8_t* base,
                                                     uint32_t raw_address,
                                                     uint32_t upload_size);
using BinkAddressRangePredicate = bool (*)(uint32_t address, uint32_t size);

struct BinkVideoUploadAddressCallbacks {
  BinkDecodeGpuPointer decode_gpu_pointer = nullptr;
  BinkSelectReadableUploadAddress select_readable_upload_address = nullptr;
  BinkAddressRangePredicate is_gpu_byte_buffer_range = nullptr;
  BinkAddressRangePredicate is_high_scratch_byte_buffer_range = nullptr;
};

struct BinkDecodedFramePlaneSource {
  uint32_t raw_address = 0;
  uint32_t address = 0;
  uint32_t row_pitch = 0;
  uint32_t byte_size = 0;
};

struct BinkDecodedFramePlaneAliases {
  std::array<std::array<uint32_t, 2>, 3> raw_addresses = {};
  std::array<std::array<uint32_t, 2>, 3> selected_addresses = {};
};

struct BinkDecodedFramePlaneMatch {
  uint32_t plane_index = UINT32_MAX;
  uint32_t frame_slot = UINT32_MAX;
};

struct BinkDecodedFrameTable {
  uint32_t frame_table = 0;
  uint32_t y_width = 0;
  uint32_t y_height = 0;
  uint32_t c_width = 0;
  uint32_t c_height = 0;
  uint32_t current_frame = UINT32_MAX;
  std::array<BinkDecodedFramePlaneSource, 3> planes = {};
  BinkDecodedFramePlaneAliases aliases;
};

uint32_t SelectBinkVideoPlaneUploadAddress(
    uint8_t* base,
    uint32_t physical_address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks);

uint32_t SelectBinkDecodedPlaneAddress(
    uint8_t* base,
    uint32_t address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks);

uint32_t SelectFableBinkReadableUploadAddress(uint8_t* base,
                                              uint32_t raw_address,
                                              uint32_t upload_size);

BinkDecodedFramePlaneMatch BinkDecodedPlaneMatchForBinding(
    uint8_t* base,
    uint32_t physical_address,
    uint32_t upload_size,
    const BinkDecodedFramePlaneAliases& aliases,
    const BinkVideoUploadAddressCallbacks& callbacks);

bool BinkDecodedFramePlaneSourceValid(
    uint8_t* base,
    const BinkDecodedFramePlaneSource& plane);

BinkVideoPlaneSetupProbe ProbeBinkVideoPlaneSetup(
    uint8_t* base,
    const BinkVideoPlaneBinding& binding,
    const BinkVideoUploadAddressCallbacks& upload_address_callbacks);

bool ReadBinkDecodedFrameTable(
    uint8_t* base,
    uint32_t frame_table,
    uint32_t max_plane_upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks,
    BinkDecodedFrameTable* table);

}  // namespace bink
}  // namespace refii::renderer
