#ifndef DATAFRAME_H
#define DATAFRAME_H

#include <stdint.h>
#include <stdbool.h>

// Data frame process callback
typedef void (*data_frame_cbk_t)(uint16_t cmd, uint16_t status, uint16_t length, uint8_t *data);

// TX buffer
typedef struct {
    uint8_t *const buffer;
    uint16_t length;
} data_frame_tx_t;

void data_frame_receive(uint8_t *data, uint16_t length);
void data_frame_process(void);
// A link change discards queued input and invalidates replies from the old session.
void data_frame_link_reset(void);
bool data_frame_response_is_current(void);
void on_data_frame_complete(data_frame_cbk_t callback);

data_frame_tx_t *data_frame_make(
    uint16_t cmd,
    uint16_t status,
    uint16_t length,
    uint8_t *data
);


#endif // DATAFRAME_H
