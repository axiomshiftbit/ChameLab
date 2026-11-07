#include "nrfx_power.h"
#include "app_timer.h"
#include "app_util_platform.h"
#include "syssleep.h"

#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"


APP_TIMER_DEF(m_app_sleep_timer);       //The timer for equipment sleep
static volatile bool m_system_off_enter = false;
static volatile bool m_system_off_active = false;

extern volatile bool g_is_ble_connected; //Link to log in BLE
extern bool g_is_tag_emulating; //The status of the logo emulation card
extern volatile bool m_is_field_on; //NFC field generator state from app_main.c


/** @brief Equipment sleep timer event
 * @param none
 * @return none
 */
static void timer_sleep_event_handle(void *arg) {
    // The sleep conditions are achieved, set the logo bit, so that the processing in the main
    m_system_off_enter = true;
}

/**@brief Sleep soft timer initialization
 */
void sleep_timer_init(void) {
    ret_code_t err_code;

    // Create a soft timer and wait for a period of time to sleep
    err_code = app_timer_create(&m_app_sleep_timer, APP_TIMER_MODE_SINGLE_SHOT, timer_sleep_event_handle);
    APP_ERROR_CHECK(err_code);
}

/**@brief Sleeping soft timer stop
 */
void sleep_timer_stop() {
    m_system_off_enter = false;
    m_system_off_active = false;
    ret_code_t err_code = app_timer_stop(m_app_sleep_timer);
    APP_ERROR_CHECK(err_code);
}

/**@brief Sleep soft timer startup
 */
void sleep_timer_start(uint32_t time_ms) {
    // Close the previous sleep timer first
    sleep_timer_stop();
    // Non -USB power supply status
    if (nrfx_power_usbstatus_get() == NRFX_POWER_USB_STATE_DISCONNECTED) {
        // If Bluetooth is still connected, or is still in the state of emulation card, or field generator is on, you don't need to start sleep
        if (g_is_ble_connected == false && g_is_tag_emulating == false && m_is_field_on == false) {
            // Start the timer
            ret_code_t err_code = app_timer_start(m_app_sleep_timer, APP_TIMER_TICKS(time_ms), NULL);
            APP_ERROR_CHECK(err_code);
        }
    }
}

/**
 *@brief Specific implementation of sleep
 *@param sysOff: The system off function implement.
 */
bool sleep_system_off_pending(void) {
    return m_system_off_active && !g_is_ble_connected && !g_is_tag_emulating &&
           !m_is_field_on && nrfx_power_usbstatus_get() == NRFX_POWER_USB_STATE_DISCONNECTED;
}

void sleep_system_run(void (*sysOffSleep)(), void (*sysOnSleep)()) {
    // Claim the expiry atomically. Activity IRQs may cancel the animation after
    // this point, so the shutdown callback must keep checking the pending flag.
    bool enter;
    CRITICAL_REGION_ENTER();
    m_system_off_active = m_system_off_enter;
    m_system_off_enter = false;
    enter = sleep_system_off_pending();
    CRITICAL_REGION_EXIT();
    if (enter) {
        sysOffSleep();
        m_system_off_active = false;
    } else {
        sysOnSleep();
    }
}
