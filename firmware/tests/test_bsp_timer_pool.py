"""An exhausted software timer pool must report failure, never a pointer past the pool."""
from pathlib import Path
import subprocess
import tempfile

src = Path(__file__).resolve().parents[1] / "application/src/bsp"
with tempfile.TemporaryDirectory(prefix="chamelab-bsp-timer-") as directory:
    temp = Path(directory)
    # The pool logic does not depend on Nordic's app_timer; model only what bsp_time.c names.
    (temp / "app_timer.h").write_text('''
#include <stdint.h>
typedef uint32_t ret_code_t;
typedef int app_timer_id_t;
#define APP_TIMER_DEF(name) static app_timer_id_t name
#define APP_TIMER_TICKS(ms) (ms)
#define APP_TIMER_MODE_REPEATED 1
#define APP_ERROR_CHECK(code) ((void)(code))
#define UNUSED_PARAMETER(value) ((void)(value))
static inline ret_code_t app_timer_create(const app_timer_id_t *id, int mode, void (*handler)(void *)) { (void)id; (void)mode; (void)handler; return 0; }
static inline ret_code_t app_timer_start(app_timer_id_t id, uint32_t ticks, void *context) { (void)id; (void)ticks; (void)context; return 0; }
static inline ret_code_t app_timer_stop(app_timer_id_t id) { (void)id; return 0; }
''')
    (temp / "test.c").write_text(r'''
#include <assert.h>
#include <stdio.h>
#include "bsp_time.h"
extern autotimer bsptimers[TIMER_BSP_COUNT];
void timer_app_callback(void *arg);
int main(void) {
    autotimer *held[TIMER_BSP_COUNT];
    for (int i = 0; i < TIMER_BSP_COUNT; i++) {
        held[i] = bsp_obtain_timer(0);
        assert(held[i] >= bsptimers && held[i] < bsptimers + TIMER_BSP_COUNT);
        for (int j = 0; j < i; j++) assert(held[j] != held[i]);
    }
    /* Every slot is taken: no pointer, an immediate timeout, and harmless set/return. */
    autotimer *none = bsp_obtain_timer(0);
    assert(none == NULL);
    assert(!NO_TIMEOUT_1MS(none, 0xFFFFFFFFu));
    assert(bsp_set_timer(none, 5) == 0);
    bsp_return_timer(none);
    for (int i = 0; i < TIMER_BSP_COUNT; i++) assert(held[i]->busy == 1);

    /* A live timer still counts in 10 ms steps and expires. */
    assert(NO_TIMEOUT_1MS(held[0], 10));
    timer_app_callback(NULL);
    assert(held[0]->time == 10 && NO_TIMEOUT_1MS(held[0], 10));
    timer_app_callback(NULL);
    assert(!NO_TIMEOUT_1MS(held[0], 10));

    /* A returned slot is handed out again, zeroed. */
    bsp_return_timer(held[3]);
    autotimer *again = bsp_obtain_timer(0);
    assert(again == held[3] && again->time == 0 && again->busy == 1);
    assert(bsp_obtain_timer(0) == NULL);
    puts("BSP timer pool: exhaustion, expiry and reuse PASS");
}
''')
    binary = temp / "test"
    subprocess.run(["gcc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-g", "-fsanitize=address,undefined",
                    "-I", str(temp), "-I", str(src), str(temp / "test.c"), str(src / "bsp_time.c"), "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True, timeout=30)
