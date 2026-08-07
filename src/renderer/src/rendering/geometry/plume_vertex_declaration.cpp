#include "runtime/plume/resource/plume_vertex_declaration.h"

#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <array>
#include <climits>
#include <cstdio>

#include <rex/logging.h>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {

uint16_t LoadBigEndianU16(const uint8_t* data) {
  return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) |
                               static_cast<uint16_t>(data[1]));
}

uint32_t LoadBigEndianU32(const uint8_t* data) {
  return (static_cast<uint32_t>(data[0]) << 24) |
         (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) |
         static_cast<uint32_t>(data[3]);
}

}  // namespace

const char* ConvertGuestDeclUsageName(uint8_t usage) {
  switch (usage) {
    case 0:
      return "POSITION";
    case 1:
      return "BLENDWEIGHT";
    case 2:
      return "BLENDINDICES";
    case 3:
      return "NORMAL";
    case 4:
      return "PSIZE";
    case 5:
      return "TEXCOORD";
    case 6:
      return "TANGENT";
    case 7:
      return "BINORMAL";
    case 8:
      return "TESSFACTOR";
    case 9:
      return "POSITIONT";
    case 10:
      return "COLOR";
    case 11:
      return "FOG";
    case 12:
      return "DEPTH";
    case 13:
      return "SAMPLE";
    default:
      return "UNKNOWN";
  }
}

uint32_t ConvertGuestDeclLocation(uint8_t usage, uint8_t usage_index) {
  struct Location {
    uint8_t usage;
    uint8_t usage_index;
    uint32_t location;
  };

  // Keep this in lock-step with XenosRecomp's USAGE_LOCATIONS table. The
  // translated shaders only emit these Vulkan locations; extending the table
  // here makes Plume feed data to locations the shader never reads.
  static constexpr std::array<Location, 17> kLocations = {{
      {0, 0, 0},   {3, 0, 1},   {6, 0, 2},   {7, 0, 3},
      {5, 0, 4},   {5, 1, 5},   {5, 2, 6},   {5, 3, 7},
      {10, 0, 8},  {2, 0, 9},   {1, 0, 10},  {10, 1, 11},
      {5, 4, 12},  {5, 5, 13},  {5, 6, 14},  {5, 7, 15},
      {0, 1, 15},
  }};

  for (const Location& location : kLocations) {
    if (location.usage == usage && location.usage_index == usage_index) {
      return location.location;
    }
  }
  // Fable 2 uses a few semantics outside the Unleashed-focused fixed table
  // above. Keep them as real declaration elements so shader-signature remapping
  // can bind them to the exact translated HLSL locations instead of falling
  // back to default vertex data.
  if (usage == 0 && usage_index < 8) {
    return 16u + usage_index;
  }
  if (usage == 5 && usage_index < 16) {
    return 24u + usage_index;
  }
  if (usage == 10 && usage_index < 4) {
    return 40u + usage_index;
  }
  return UINT32_MAX;
}

plume::RenderFormat ConvertGuestDeclType(uint32_t type) {
  switch (type) {
    case 0x2C83A4:
      return plume::RenderFormat::R32_FLOAT;
    case 0x2C23A5:
      return plume::RenderFormat::R32G32_FLOAT;
    case 0x2A23B9:
      return plume::RenderFormat::R32G32B32_FLOAT;
    case 0x1A23A6:
      return plume::RenderFormat::R32G32B32A32_FLOAT;
    case 0x182886:
      return plume::RenderFormat::B8G8R8A8_UNORM;
    case 0x1A2286:
    case 0x1A2386:
      return plume::RenderFormat::R8G8B8A8_UINT;
    case 0x2C2359:
      return plume::RenderFormat::R16G16_SINT;
    case 0x1A235A:
      return plume::RenderFormat::R16G16B16A16_SINT;
    case 0x1A2086:
    case 0x1A2186:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case 0x2C2159:
      return plume::RenderFormat::R16G16_SNORM;
    case 0x1A215A:
      return plume::RenderFormat::R16G16B16A16_SNORM;
    case 0x2C2059:
      return plume::RenderFormat::R16G16_UNORM;
    case 0x1A205A:
      return plume::RenderFormat::R16G16B16A16_UNORM;
    case 0x2C82A1:
    case 0x2C83A1:
      return plume::RenderFormat::R32_UINT;
    case 0x2C23A2:
      return plume::RenderFormat::R32G32_UINT;
    case 0x1A23A3:
      return plume::RenderFormat::R32G32B32A32_UINT;
    case 0x1A2187:
    case 0x1A2287:
    case 0x2A2187:
    case 0x2A2287:
    case 0x2A2190:
    case 0x2A2390:
      return plume::RenderFormat::R32_UINT;
    case 0x2C235F:
      return plume::RenderFormat::R16G16_FLOAT;
    case 0x1A2360:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    default:
      return plume::RenderFormat::UNKNOWN;
  }
}

bool GuestDeclTypeNeedsSwappedTexcoords(uint32_t type) {
  switch (type) {
    case 0x2C2359:  // SHORT2
    case 0x1A235A:  // SHORT4
    case 0x2C2159:  // SHORT2N
    case 0x1A215A:  // SHORT4N
    case 0x2C2059:  // USHORT2N
    case 0x1A205A:  // USHORT4N
    case 0x2C235F:  // FLOAT16_2
    case 0x1A2360:  // FLOAT16_4
      return true;
    default:
      return false;
  }
}

bool DecodeVertexDeclaration(PlumeTrackedResource& tracked,
                             const RendererTrackedResourceSnapshot& snapshot) {
  tracked.guest_vertex_declaration.input_slots.clear();
  tracked.guest_vertex_declaration.input_elements.clear();
  tracked.debug_summary.clear();
  tracked.swapped_texcoords = 0;
  tracked.has_r11g11b10_normal = false;
  if (snapshot.kind != RendererTrackedResourceKind::kVertexDeclaration ||
      snapshot.upload_data.empty() || snapshot.upload_data.size() % 12 != 0) {
    return false;
  }

  std::array<bool, 16> used_slots = {};
  const size_t upload_element_count = snapshot.upload_data.size() / 12;
  const size_t element_count =
      snapshot.vertex_declaration.element_count != 0
          ? std::min<size_t>(snapshot.vertex_declaration.element_count,
                             upload_element_count)
          : upload_element_count;
  tracked.guest_vertex_declaration.input_elements.reserve(element_count);

  for (size_t i = 0; i < element_count; ++i) {
    const uint8_t* element = snapshot.upload_data.data() + i * 12;
    const uint16_t stream = LoadBigEndianU16(element + 0);
    const uint16_t offset = LoadBigEndianU16(element + 2);
    const uint32_t type = LoadBigEndianU32(element + 4);
    const uint8_t usage = element[9];
    const uint8_t usage_index = element[10];
    if (stream == 0xFF || type == 0xFFFFFFFFu || stream >= used_slots.size()) {
      continue;
    }

    plume::RenderFormat format = ConvertGuestDeclType(type);
    if (format == plume::RenderFormat::UNKNOWN) {
      REXLOG_WARN(
          "Refii plume vertex declaration rejected unknown element type "
          "guest=0x{:08X} type=0x{:08X} usage={} usage_index={}",
          snapshot.guest_address, type, usage, usage_index);
      continue;
    }

    if (usage == 3 || usage == 6 || usage == 7) {
      if (type == 0x2A23B9) {
        format = plume::RenderFormat::R32G32B32_UINT;
      } else {
        tracked.has_r11g11b10_normal = true;
      }
    }
    if (usage == 5 && usage_index < 32 &&
        GuestDeclTypeNeedsSwappedTexcoords(type)) {
      tracked.swapped_texcoords |= 1u << usage_index;
    }

    const uint32_t location = ConvertGuestDeclLocation(usage, usage_index);
    if (location == UINT32_MAX) {
      REXLOG_WARN(
          "Refii plume vertex declaration rejected unknown semantic "
          "guest=0x{:08X} usage={} usage_index={}",
          snapshot.guest_address, usage, usage_index);
      continue;
    }

    tracked.guest_vertex_declaration.input_elements.emplace_back(
        ConvertGuestDeclUsageName(usage), usage_index, location, format, stream,
        offset);
    char element_summary[192] = {};
    std::snprintf(
        element_summary, sizeof(element_summary),
        "%s#%zu:s%u+%u/t%08X/u%u.%u/l%u/f%u raw=%02X%02X%02X%02X%02X%02X"
        "%02X%02X%02X%02X%02X%02X",
        tracked.debug_summary.empty() ? "" : ";", i, stream, offset, type,
        usage, usage_index, location, static_cast<uint32_t>(format),
        element[0], element[1], element[2], element[3], element[4],
        element[5], element[6], element[7], element[8], element[9],
        element[10], element[11]);
    tracked.debug_summary += element_summary;
    used_slots[stream] = true;
  }

  for (uint32_t slot = 0; slot < used_slots.size(); ++slot) {
    if (used_slots[slot]) {
      tracked.guest_vertex_declaration.input_slots.emplace_back(slot, 0);
    }
  }

  return !tracked.guest_vertex_declaration.input_elements.empty();
}
#endif

}  // namespace refii::renderer
