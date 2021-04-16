#include <stdlib.h>

#include <ziti/ziti.h>
#include <uv.h>
#include "ziti4dotnet.h"

#if _WIN32
#define strncasecmp _strnicmp
#define strdup _strdup
#endif

int z4d_ziti_close(ziti_connection con) {
    return 0;
    //return ziti_close(&con);
}

int z4d_uv_run(void* loop) {
    ZITI_LOG(TRACE, "running loop with address: %p", loop);
    return uv_run(loop, UV_RUN_DEFAULT);
}

const char* ALL_CONFIG_TYPES[] = {
        "all",
        NULL
};
extern const char** z4d_all_config_types() {
    return ALL_CONFIG_TYPES;
}

uv_loop_t* z4d_default_loop()
{
    return uv_default_loop();
}

void* z4d_registerUVTimer(uv_loop_t * loop, uv_timer_cb timer_cb, uint64_t delay, uint64_t iterations) {
    uv_timer_t * uvt = calloc(1, sizeof(uv_timer_t));
    uv_timer_init(loop, uvt);
    uv_timer_start(uvt, timer_cb, iterations, delay);
    return uvt;
}

void* newLoop() {
    return uv_loop_new();
}

int ziti_event_type_from_pointer(const ziti_event_t *event) {
    return event->type;
}

ziti_service* ziti_service_array_get(ziti_service_array arr, int idx) {
    return arr ? arr[idx] : NULL;
}