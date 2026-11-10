"""Concurrent producer/consumer on the real LF queue, plus wrap/full/empty checks."""
from pathlib import Path
import subprocess
import tempfile

src = Path(__file__).resolve().parents[1] / "application/src/rfid/nfctag/lf/utils"
with tempfile.TemporaryDirectory(prefix="chamelab-lf-queue-") as directory:
    temp = Path(directory)
    # Model Nordic's application interrupt exclusion with a host mutex.
    (temp / "app_util_platform.h").write_text('''
#include <pthread.h>
extern pthread_mutex_t irq_mutex;
#define CRITICAL_REGION_ENTER() { pthread_mutex_lock(&irq_mutex);
#define CRITICAL_REGION_EXIT() pthread_mutex_unlock(&irq_mutex); }
''')
    (temp / "test.c").write_text(r'''
#define _POSIX_C_SOURCE 200809L
#include <assert.h>
#include <pthread.h>
#include <sched.h>
#include <stdint.h>
#include <stdio.h>
#include "circular_buffer.h"
pthread_mutex_t irq_mutex = PTHREAD_MUTEX_INITIALIZER;
static circular_buffer cb;
static void *producer(void *unused) {
    for(uint32_t i=0;i<1000000;i++) while(!cb_push_back(&cb,&i)) sched_yield();
    return NULL;
}
int main(void) {
    assert(cb_init(&cb,7,sizeof(uint32_t)));
    uint32_t out;
    assert(!cb_pop_front(&cb,&out));
    for(uint32_t i=0;i<7;i++) assert(cb_push_back(&cb,&i));
    assert(!cb_push_back(&cb,&out));
    for(uint32_t i=0;i<7;i++) { assert(cb_pop_front(&cb,&out));assert(out==i); }
    pthread_t thread;assert(pthread_create(&thread,NULL,producer,NULL)==0);
    for(uint32_t i=0;i<1000000;i++) {
        while(!cb_pop_front(&cb,&out)) sched_yield();
        assert(out==i);
    }
    assert(pthread_join(thread,NULL)==0);
    assert(cb.count==0);cb_free(&cb);
    assert(!cb_pop_front(&cb,&out) && !cb_push_back(&cb,&out));
    puts("LF queue: 1000000 ordered samples PASS");
}
''')
    binary = temp / "test"
    subprocess.run(["gcc", "-std=c99", "-O2", "-g", "-pthread", "-fno-pie", "-no-pie", "-fsanitize=address,undefined", "-I", str(temp), "-I", str(src), str(temp / "test.c"), str(src / "circular_buffer.c"), "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True, timeout=30)
