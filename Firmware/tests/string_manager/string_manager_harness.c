#include <stdio.h>
#include <string.h>
#include <assert.h>

#include "string_manager.h"
#include "proto_frame.h"
#include "hid_host.h"

#define STRING_REQ_FALLBACK_MS_VALUE 10
#define EXTRA_FETCH_TIMEOUT_MS_VALUE 75

// ------------------------------------------------------------------
// TinyUSB host stubs
// ------------------------------------------------------------------

typedef struct
{
    bool     armed;
    bool     succeed;
    bool     defer_callback;
    uint8_t  expect_index;
    uint16_t expect_lang;
    uint8_t  data[PROXY_STRING_DESC_MAX];
    uint16_t len;
    tuh_xfer_cb_t pending_cb;
    uintptr_t    pending_user_data;
    uint8_t*     pending_buffer;
    uint16_t     pending_bufsize;
    uint8_t      pending_daddr;
} fetch_stub_t;

static fetch_stub_t g_fetch_stub;
static uint32_t     g_fetch_invocations;

void harness_prepare_extra_string(uint8_t index,
                                  uint16_t langid,
                                  const uint8_t* desc,
                                  uint16_t len,
                                  bool succeed,
                                  bool defer_callback)
{
    memset(&g_fetch_stub, 0, sizeof(g_fetch_stub));
    g_fetch_stub.armed          = true;
    g_fetch_stub.succeed        = succeed;
    g_fetch_stub.defer_callback = defer_callback;
    g_fetch_stub.expect_index   = index;
    g_fetch_stub.expect_lang    = langid;
    if (desc && len)
    {
        if (len > sizeof(g_fetch_stub.data))
        {
            len = sizeof(g_fetch_stub.data);
        }
        memcpy(g_fetch_stub.data, desc, len);
        g_fetch_stub.len = len;
    }
}

static void harness_complete_deferred_fetch(bool success)
{
    if (!g_fetch_stub.pending_cb)
    {
        return;
    }

    tuh_xfer_t xfer = {
        .daddr      = g_fetch_stub.pending_daddr,
        .result     = success ? XFER_RESULT_SUCCESS : XFER_RESULT_FAILED,
        .actual_len = success ? g_fetch_stub.len : 0,
        .buffer     = g_fetch_stub.pending_buffer,
        .user_data  = g_fetch_stub.pending_user_data
    };

    if (success &&
        g_fetch_stub.pending_buffer &&
        g_fetch_stub.len <= g_fetch_stub.pending_bufsize)
    {
        memcpy(g_fetch_stub.pending_buffer, g_fetch_stub.data, g_fetch_stub.len);
    }

    g_fetch_stub.pending_cb(&xfer);
    g_fetch_stub.pending_cb       = NULL;
    g_fetch_stub.pending_buffer   = NULL;
    g_fetch_stub.pending_user_data = 0;
}

bool tuh_descriptor_get_string(uint8_t dev_addr,
                               uint8_t index,
                               uint16_t langid,
                               uint8_t* buffer,
                               uint16_t bufsize,
                               tuh_xfer_cb_t complete_cb,
                               uintptr_t user_data)
{
    (void)dev_addr;
    g_fetch_invocations++;

    if (!g_fetch_stub.armed ||
        index != g_fetch_stub.expect_index ||
        langid != g_fetch_stub.expect_lang)
    {
        return false;
    }

    g_fetch_stub.armed = false;

    if (g_fetch_stub.defer_callback)
    {
        g_fetch_stub.pending_cb        = complete_cb;
        g_fetch_stub.pending_user_data = user_data;
        g_fetch_stub.pending_buffer    = buffer;
        g_fetch_stub.pending_bufsize   = bufsize;
        g_fetch_stub.pending_daddr     = dev_addr;
        return true;
    }

    tuh_xfer_t xfer = {
        .daddr      = dev_addr,
        .result     = g_fetch_stub.succeed ? XFER_RESULT_SUCCESS : XFER_RESULT_FAILED,
        .actual_len = g_fetch_stub.succeed ? g_fetch_stub.len : 0,
        .buffer     = buffer,
        .user_data  = user_data
    };

    if (g_fetch_stub.succeed && buffer && g_fetch_stub.len <= bufsize)
    {
        memcpy(buffer, g_fetch_stub.data, g_fetch_stub.len);
    }

    if (complete_cb)
    {
        complete_cb(&xfer);
    }
    return true;
}

// ------------------------------------------------------------------
// Harness plumbing
// ------------------------------------------------------------------

typedef struct
{
    uint8_t cmd;
    uint16_t len;
    uint8_t payload[PROTO_MAX_PAYLOAD_SIZE];
} frame_record_t;

static frame_record_t g_frames[32];
static size_t         g_frame_count;
static uint32_t       g_now_ms;

proxy_hid_t g_hid = { 0 };

static void clear_frames(void)
{
    memset(g_frames, 0, sizeof(g_frames));
    g_frame_count = 0;
}

static bool harness_send_frames(uint8_t cmd, const uint8_t* data, uint16_t len)
{
    if (g_frame_count < sizeof(g_frames) / sizeof(g_frames[0]))
    {
        frame_record_t* rec = &g_frames[g_frame_count++];
        rec->cmd = cmd;
        rec->len = len;
        if (len > sizeof(rec->payload))
        {
            len = sizeof(rec->payload);
        }
        if (data && len)
        {
            memcpy(rec->payload, data, len);
        }
    }
    return true;
}

static uint32_t harness_time_ms(void)
{
    return g_now_ms;
}

static void advance_time(uint32_t delta)
{
    g_now_ms += delta;
}

static void harness_reset_state(void)
{
    clear_frames();
    g_now_ms            = 0;
    g_fetch_invocations = 0;
    memset(&g_fetch_stub, 0, sizeof(g_fetch_stub));

    g_hid.dev_addr = 1; // emulate attached device by default
    string_manager_reset();
}

static void dump_frame(const frame_record_t* rec)
{
    printf("  cmd=%u len=%u data:", rec->cmd, rec->len);
    for (uint16_t i = 0; i < rec->len && i < 12; ++i)
    {
        printf(" %02X", rec->payload[i]);
    }
    if (rec->len > 12)
    {
        printf(" ...");
    }
    printf("\n");
}

static uint16_t build_utf16_string(const char* ascii, uint8_t* out, size_t max_len)
{
    size_t chars = strlen(ascii);
    uint16_t total = (uint16_t)(2 + chars * 2);
    assert(total <= max_len);
    out[0] = (uint8_t)total;
    out[1] = TUSB_DESC_STRING;
    for (size_t i = 0; i < chars; ++i)
    {
        out[2 + (i * 2)] = (uint8_t)ascii[i];
        out[3 + (i * 2)] = 0;
    }
    return total;
}

// ------------------------------------------------------------------
// Tests
// ------------------------------------------------------------------

static bool test_cached_string_flow(void)
{
    harness_reset_state();
    string_manager_set_default_lang(0x0409);

    uint8_t desc1[PROXY_STRING_DESC_MAX];
    uint16_t len1 = build_utf16_string("Logitech", desc1, sizeof(desc1));
    string_manager_cache_store(1, 0x0409, desc1, len1);

    uint8_t payload[] = { 1, 0x09, 0x04 };
    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    if (g_frame_count != 1)
    {
        printf("[FAIL] cached string flow produced %zu frames\n", g_frame_count);
        return false;
    }

    const frame_record_t* rec = &g_frames[0];
    bool ok = (rec->cmd == PF_DESC_STRING &&
               rec->len == len1 + 1 &&
               rec->payload[0] == 1 &&
               memcmp(&rec->payload[1], desc1, len1) == 0 &&
               g_fetch_invocations == 0);
    if (!ok)
    {
        printf("[FAIL] cached string payload mismatch / stray fetch\n");
        dump_frame(rec);
    }
    return ok;
}

static bool test_extra_fetch_success(void)
{
    harness_reset_state();
    static const char* kStr = "Proxy";
    uint8_t probe_desc[PROXY_STRING_DESC_MAX];
    uint16_t probe_len = build_utf16_string(kStr, probe_desc, sizeof(probe_desc));

    harness_prepare_extra_string(1, 0x0409, probe_desc, probe_len, true, false);

    uint8_t payload[] = { 1, 0x09, 0x04 };
    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    bool ok = (g_fetch_invocations == 1);
    if (!ok)
    {
        printf("[FAIL] extra fetch was not invoked (count=%u)\n",
               (unsigned)g_fetch_invocations);
        return false;
    }

    if (g_frame_count != 1)
    {
        printf("[FAIL] extra fetch did not emit descriptor frame\n");
        return false;
    }

    const frame_record_t* rec = &g_frames[0];
    ok = (rec->cmd == PF_DESC_STRING &&
          rec->payload[0] == 1 &&
          rec->len == probe_len + 1 &&
          memcmp(&rec->payload[1], probe_desc, probe_len) == 0);
    if (!ok)
    {
        printf("[FAIL] extra fetch payload mismatch\n");
        dump_frame(rec);
    }
    return ok;
}

static bool test_high_index_empty_descriptor(void)
{
    harness_reset_state();
    uint8_t payload[] = { 5, 0x09, 0x04 };
    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    if (g_frame_count != 1)
    {
        printf("[FAIL] high index fallback generated %zu frames\n", g_frame_count);
        return false;
    }

    const frame_record_t* rec = &g_frames[0];
    bool ok = (rec->cmd == PF_DESC_STRING &&
               rec->len == 3 &&
               rec->payload[0] == 5 &&
               rec->payload[1] == 0x02 &&
               rec->payload[2] == TUSB_DESC_STRING);
    if (!ok)
    {
        printf("[FAIL] high index fallback payload mismatch\n");
        dump_frame(rec);
    }
    return ok;
}

static bool test_timeout_fallback_idx1(void)
{
    harness_reset_state();
    string_manager_set_default_lang(0x0409);
    harness_prepare_extra_string(1, 0x0409, NULL, 0, true, true);

    uint8_t payload[] = { 1, 0x09, 0x04 };
    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    if (g_frame_count != 0)
    {
        printf("[FAIL] fallback test emitted frame before timeout\n");
        return false;
    }

    advance_time(STRING_REQ_FALLBACK_MS_VALUE);
    string_manager_task();

    if (g_frame_count != 1)
    {
        printf("[FAIL] fallback test expected synthetic descriptor\n");
        return false;
    }

    const frame_record_t* rec = &g_frames[0];
    bool ok = (rec->payload[0] == 1 &&
               rec->payload[1] >= 0x04 &&
               rec->payload[2] == TUSB_DESC_STRING);
    if (!ok)
    {
        printf("[FAIL] synthetic descriptor payload mismatch\n");
        dump_frame(rec);
        return false;
    }

    clear_frames();

    // second request should produce empty descriptor
    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    if (g_frame_count != 1)
    {
        printf("[FAIL] empty descriptor phase missing frame\n");
        return false;
    }

    rec = &g_frames[0];
    ok = (rec->payload[0] == 1 &&
          rec->len == 3 &&
          rec->payload[1] == 0x02 &&
          rec->payload[2] == TUSB_DESC_STRING);
    if (!ok)
    {
        printf("[FAIL] empty descriptor payload mismatch\n");
        dump_frame(rec);
        return false;
    }

    advance_time(EXTRA_FETCH_TIMEOUT_MS_VALUE);
    string_manager_task();
    harness_complete_deferred_fetch(false);
    return true;
}

static bool test_cache_eviction_triggers_fetch(void)
{
    harness_reset_state();
    string_manager_set_default_lang(0x0409);

    uint8_t cached_desc[PROXY_STRING_DESC_MAX];
    uint16_t cached_len = build_utf16_string("Alpha", cached_desc, sizeof(cached_desc));
    string_manager_cache_store(1, 0x0409, cached_desc, cached_len);

    uint8_t payload[] = { 1, 0x09, 0x04 };
    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    if (g_frame_count != 1)
    {
        printf("[FAIL] cached descriptor missing before eviction\n");
        return false;
    }
    const frame_record_t* rec = &g_frames[0];
    if (memcmp(&rec->payload[1], cached_desc, cached_len) != 0)
    {
        printf("[FAIL] cached descriptor payload mismatch\n");
        dump_frame(rec);
        return false;
    }

    clear_frames();

    // Fill remaining slots (capacity = 16)
    for (uint8_t idx = 2; idx < 2 + 15; ++idx)
    {
        char name[8];
        snprintf(name, sizeof(name), "E%u", idx);
        uint8_t desc[PROXY_STRING_DESC_MAX];
        uint16_t len = build_utf16_string(name, desc, sizeof(desc));
        string_manager_cache_store(idx, 0x0409, desc, len);
    }

    uint8_t remote_desc[PROXY_STRING_DESC_MAX];
    uint16_t remote_len = build_utf16_string("Beta", remote_desc, sizeof(remote_desc));
    harness_prepare_extra_string(1, 0x0409, remote_desc, remote_len, true, false);

    string_manager_handle_ctrl_request(payload, sizeof(payload));
    string_manager_task();

    if (g_fetch_invocations != 1)
    {
        printf("[FAIL] eviction did not trigger remote fetch (count=%u)\n",
               (unsigned)g_fetch_invocations);
        return false;
    }

    if (g_frame_count != 1)
    {
        printf("[FAIL] eviction fetch did not send descriptor\n");
        return false;
    }

    rec = &g_frames[0];
    bool ok = (rec->cmd == PF_DESC_STRING &&
               rec->payload[0] == 1 &&
               rec->len == remote_len + 1 &&
               memcmp(&rec->payload[1], remote_desc, remote_len) == 0);
    if (!ok)
    {
        printf("[FAIL] eviction descriptor payload mismatch\n");
        dump_frame(rec);
    }
    return ok;
}

int main(void)
{
    string_manager_ops_t ops = {
        .send_frames = harness_send_frames,
        .time_ms     = harness_time_ms
    };
    string_manager_init(&ops);

    struct
    {
        const char* name;
        bool (*fn)(void);
    } tests[] = {
        { "cached string flow",            test_cached_string_flow },
        { "extra fetch success",           test_extra_fetch_success },
        { "idx>2 empty descriptor",        test_high_index_empty_descriptor },
        { "timeout fallback idx=1",        test_timeout_fallback_idx1 },
        { "cache eviction triggers fetch", test_cache_eviction_triggers_fetch }
    };

    size_t total  = sizeof(tests) / sizeof(tests[0]);
    size_t passed = 0;

    for (size_t i = 0; i < total; ++i)
    {
        bool ok = tests[i].fn();
        printf("%s: %s\n", tests[i].name, ok ? "PASS" : "FAIL");
        if (ok) passed++;
    }

    printf("\nSummary: %zu/%zu tests passed\n", passed, total);
    return (passed == total) ? 0 : 1;
}
