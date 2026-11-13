// librefii_codegen.so: the ReXGlue generator's command line, run by the launcher as a child process.
// A child started from the app does not inherit the app's runtime setup, so the generator sets up
// the Android platform layer itself around the SDK's own main (renamed rexglue_cli_main here).
#include <rex/main_android.h>

int rexglue_cli_main(int argc, char** argv);

int main(int argc, char** argv) {
  rex::InitializeAndroidPlatform();
  int status = rexglue_cli_main(argc, argv);
  rex::ShutdownAndroidPlatform();
  return status;
}
