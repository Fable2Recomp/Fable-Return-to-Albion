#pragma once

#include <cstdint>
#include <string>

namespace refii::renderer {

struct RenderDocDebuggerStatus {
  bool compiled = false;
  bool enabled = false;
  bool probe_attempted = false;
  bool available = false;
  bool capturing = false;
  bool capture_request_pending = false;
  bool replay_launch_enabled = false;
  int api_major = 0;
  int api_minor = 0;
  int api_patch = 0;
  uint32_t capture_count = 0;
  std::string loaded_path;
  std::string capture_template;
  std::string last_error;
};

bool RenderDocDebuggerAvailable();
RenderDocDebuggerStatus GetRenderDocDebuggerStatus();
std::string RenderDocCaptureDirectory();
void InitializeRenderDocDebugger();
void PrepareRenderDocVulkanLayer();
void TriggerRenderDocCapture();
bool RenderDocFrameCaptureActive();
bool BeginRenderDocImmediateFrameCapture(const char* title);
bool BeginRenderDocFrameCapture(const char* title, bool ready_to_capture);
void SetRenderDocFrameCaptureFileTag(const char* tag);
void SetRenderDocFrameCaptureTitle(const char* title);
void SetRenderDocFrameCaptureComments(const char* comments);
void EndRenderDocFrameCapture(bool capture_started);
void PollRenderDocCaptureRequest();
// True when <capture_dir>/capture_render existed (consumed as ack): the
// caller should wrap the next full present-pump iteration in an immediate
// frame capture — the compose-trace channel for full draw-stream captures.
bool ConsumeRenderDocComposeTraceRequest();

}  // namespace refii::renderer
