/* Exercise the real shared SDL Android event pump without SDLActivity/JNI.
 * Run in a disposable app-UID or shell process with the matching SDL closure.
 * The watchdog makes a missing lifecycle guard terminate instead of spinning.
 */
#define SDL_MAIN_HANDLED 1
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>

#include <inttypes.h>
#include <signal.h>
#include <stdio.h>
#include <unistd.h>

static unsigned failures;

static void check(const char *label, bool passed)
{
    printf("%s %s\n", passed ? "PASS" : "FAIL", label);
    fflush(stdout);
    failures += !passed;
}

static void timeout_handler(int signal_number)
{
    (void)signal_number;
    static const char message[] = "FAIL SDL embedded event pump watchdog\n";
    (void)write(STDERR_FILENO, message, sizeof(message) - 1);
    _exit(124);
}

static void exercise_queue(const char *phase)
{
    const Uint32 type = SDL_RegisterEvents(1);
    check("register user event", type != 0);
    SDL_Event sent = {0};
    sent.type = type;
    sent.user.code = 0x52454649;
    check("push ordinary user event", SDL_PushEvent(&sent));

    bool received = false;
    SDL_Event event;
    Uint64 started = SDL_GetTicksNS();
    for (unsigned i = 0; i < 1024; ++i) {
        if (SDL_PollEvent(&event) && event.type == type &&
            event.user.code == sent.user.code) {
            received = true;
        }
    }
    const Uint64 poll_ns = SDL_GetTicksNS() - started;
    check("PollEvent returns and preserves user event", received);
    check("1024 poll calls complete within one second", poll_ns < SDL_NS_PER_SECOND);

    /* Consume every pending event before checking a timed empty wait. */
    for (unsigned i = 0; i < 1024 && SDL_PollEvent(&event); ++i) {
    }
    started = SDL_GetTicksNS();
    const bool unexpected_event = SDL_WaitEventTimeout(&event, 20);
    const Uint64 wait_ns = SDL_GetTicksNS() - started;
    check("empty timed wait returns false", !unexpected_event);
    check("timed wait is bounded and actually waits", wait_ns >= SDL_MS_TO_NS(5) &&
          wait_ns < SDL_NS_PER_SECOND);
    printf("TIMING phase=%s poll_ns=%" PRIu64 " wait_ns=%" PRIu64 "\n",
           phase, poll_ns, wait_ns);
    fflush(stdout);
}

int main(void)
{
    signal(SIGALRM, timeout_handler);
    alarm(5);
    SDL_SetMainReady();
    SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "dummy");
    SDL_SetHint(SDL_HINT_AUDIO_DRIVER, "dummy");
    printf("SDL embedded probe: no SDLActivity nativeSetupJNI; version=%d\n",
           SDL_GetVersion());
    fflush(stdout);
    if (!SDL_Init(SDL_INIT_EVENTS)) {
        fprintf(stderr, "FAIL InitEvents: %s\n", SDL_GetError());
        return 1;
    }
    /* With no video subsystem the guard must find the explicit driver hint. */
    exercise_queue("events_only");
    if (!SDL_InitSubSystem(SDL_INIT_VIDEO)) {
        fprintf(stderr, "FAIL InitVideo(dummy): %s\n", SDL_GetError());
        return 1;
    }
    check("actual selected video driver is dummy",
          SDL_strcmp(SDL_GetCurrentVideoDriver(), "dummy") == 0);
    exercise_queue("dummy_video");
    SDL_Quit();
    alarm(0);
    printf("%s SDL embedded event pump: failures=%u\n",
           failures ? "FAIL" : "PASS", failures);
    return failures ? 1 : 0;
}
