"""Inject activity during the real shutdown animation, before hardware teardown."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1] / "application/src"
sleep = (root / "utils/syssleep.c").read_text()
sleep = sleep[sleep.index("APP_TIMER_DEF"):]
main = (root / "app_main.c").read_text()
main = main[main.index("static void system_off_enter(void) {"):]
# Hardware pin teardown is represented by a counter; the cancellation decisions
# and animations preceding it are compiled unchanged from production.
main = main[:main.index("    // Disable the HF NFC event first")] + "    shutdowns++;\n}\n"
prefix = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
typedef int ret_code_t;
#define APP_TIMER_DEF(x) static int x
#define APP_TIMER_MODE_SINGLE_SHOT 0
#define APP_TIMER_TICKS(x) (x)
#define APP_ERROR_CHECK(x) assert((x)==0)
#define NRFX_POWER_USB_STATE_DISCONNECTED 0
#define CRITICAL_REGION_ENTER() do {} while(0)
#define CRITICAL_REGION_EXIT() do {} while(0)
#define RGB_RED 1
#define RGB_HEX_GREEN 2
#define RGB_HEX_BLUE 3
#define LED_1 1
#define RGB_LIST_NUM 8
#define NRF_POWER_RESETREAS_NFC_MASK 1
#define NRF_POWER_RESETREAS_LPCOMP_MASK 2
#define SettingsAnimationModeFull 0
#define SettingsAnimationModeMinimal 1
#define SettingsAnimationModeSymmetric 2
static bool m_system_off_processing, g_is_low_battery_shutdown;
volatile bool g_is_ble_connected, m_is_field_on;
bool g_is_tag_emulating;
static uint32_t m_reset_source, leds[8];
static int usb, mode, hook, shutdowns, restores, animation, starts;
static bool requested=true, save_fail;
static int nrfx_power_usbstatus_get(void) { return usb; }
static int app_timer_create(int *t,int m,void (*cb)(void *)) { return 0; }
static int app_timer_stop(int t) { return 0; }
static int app_timer_start(int t,int ms,void *arg) { starts++;return 0; }
static void sleep_timer_stop(void);
static void activity(int stage) {
    if(hook != stage) return;
    if(mode==1) g_is_ble_connected=true;
    else if(mode==2) usb=1;
    else if(mode==3) g_is_tag_emulating=true;
    else m_system_off_processing=false;
    sleep_timer_stop();
}
static bool tag_emulation_save(void) { activity(1); return !save_fail; }
static void rgb_marquee_stop(void) {}
static void hw_rgb_pwm_release(void) {}
static void set_slot_light_color(int c) {}
static void nrf_gpio_pin_set(int p) {}
static void nrf_gpio_pin_clear(int p) {}
static void bsp_delay_ms(int ms) { activity(2); }
static uint32_t *hw_get_led_array(void) { return leds; }
static uint8_t settings_get_animation_config(void) { return animation; }
static uint8_t tag_emulation_get_slot(void) { return 0; }
static uint32_t get_color_by_slot(uint8_t s) { return 0; }
static void rgb_marquee_sweep_from_to(int c,int s,int d) { activity(2); }
static void rgb_marquee_sweep_fade(int c,int d,int a,int b,int e) { activity(2); }
static void rgb_marquee_symmetric_in(int c,int s) { activity(2); }
static void light_up_by_slot(void) { restores++; }
static int settings_get_sleep_timeout(void) { return 1000; }
static bool advertising_is_requested(void) { return requested; }
static void advertising_stop(void) { requested=false; activity(3); }
static void advertising_start(bool erase) { requested=true; }
static void system_on(void) {}
'''
tests = r'''
int main(void) {
    for (int low=0;low<2;low++) for(animation=0;animation<3;animation++)
    for (int prior=0;prior<2;prior++) for(mode=0;mode<=4;mode++)
    for(hook=1;hook<=3;hook++) {
        requested=prior; g_is_low_battery_shutdown=low;
        g_is_ble_connected=g_is_tag_emulating=false; usb=0;
        shutdowns=restores=starts=0;
        timer_sleep_event_handle(NULL);
        if(mode==0) hook+=10; // idle: no cancellation
        sleep_system_run(system_off_enter,system_on);
        if(mode==0) { assert(shutdowns==1); assert(!requested); hook-=10; }
        else { assert(shutdowns==0); assert(restores==1); assert(requested==prior); }
    }
    // A queued expiry cannot override activity already visible to the main loop.
    g_is_ble_connected=true; shutdowns=0;
    timer_sleep_event_handle(NULL);
    sleep_system_run(system_off_enter,system_on);
    assert(shutdowns==0);
    g_is_ble_connected=false; mode=0; hook=0; save_fail=true;
    g_is_low_battery_shutdown=false; shutdowns=starts=0;
    timer_sleep_event_handle(NULL);
    sleep_system_run(system_off_enter,system_on);
    assert(shutdowns==0 && starts==1 && !m_system_off_processing);
    // An empty battery cannot keep RAM powered indefinitely after a write failure.
    g_is_low_battery_shutdown=true;
    timer_sleep_event_handle(NULL);
    sleep_system_run(system_off_enter,system_on);
    assert(shutdowns==1);
    puts("Shutdown cancellation: 180 interleavings and flash failure PASS");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-sleep-") as directory:
    temp = Path(directory)
    source = temp / "test.c"
    source.write_text(prefix + sleep + main + tests)
    binary = temp / "test"
    subprocess.run(["gcc", "-std=c99", "-O1", "-g", "-fno-pie", "-no-pie", "-fsanitize=address,undefined", "-fno-sanitize-recover=all", str(source), "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True)
