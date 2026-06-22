#pragma once

namespace refii::renderer {

bool RenderDocDebuggerAvailable();
void InitializeRenderDocDebugger();
void PrepareRenderDocVulkanLayer();
void TriggerRenderDocCapture();
bool BeginRenderDocImmediateFrameCapture(const char* title);
bool BeginRenderDocFrameCapture(const char* title, bool ready_to_capture);
void SetRenderDocFrameCaptureTitle(const char* title);
void SetRenderDocFrameCaptureComments(const char* comments);
void EndRenderDocFrameCapture(bool capture_started);
void PollRenderDocCaptureRequest();

}  // namespace refii::renderer
