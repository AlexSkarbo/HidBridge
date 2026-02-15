#include "string_manager.h"

#include "hid_proxy_host.h"
#include "proto_frame.h"
#include "logging.h"
#include "tusb.h"

#include <string.h>
#include <stdio.h>

#define STRING_CACHE_CAPACITY   16
#define STRING_REQ_QUEUE_LEN     8
#define EXTRA_FETCH_QUEUE_LEN    4
#define EXTRA_FETCH_TIMEOUT_MS 150
#define STRING_REQ_FALLBACK_MS 180
#define STRING_FETCH_MAX_RETRIES 5
#define STRING_FALLBACK_ENABLED  1  // 1 = synthesize strings when fetch fails

typedef struct
{
    bool     pending;
    uint8_t  index;
    uint16_t langid;
    uint16_t len;
    uint8_t  data[PROXY_STRING_DESC_MAX];
} pending_string_desc_t;

typedef struct
{
    bool     valid;
    uint8_t  index;
    uint16_t langid;
    uint16_t len;
    bool     synthetic;
    bool     synthetic_sent;
    uint8_t  data[PROXY_STRING_DESC_MAX];
} cached_string_desc_t;

typedef struct
{
    bool     pending;
    bool     fetching;
    uint8_t  index;
    uint16_t langid;
    uint32_t fetch_start_ms;
    bool     fallback_sent;
    uint8_t  retry_count;
} pending_string_request_t;

typedef struct
{
    bool     queued;
    bool     active;
    uint8_t  index;
    uint16_t langid;
    uint32_t start_ms;
    uint8_t  buffer[PROXY_STRING_DESC_MAX];
} extra_string_fetch_t;

static string_manager_ops_t s_ops;
static uint16_t s_default_langid = 0;
static cached_string_desc_t s_string_cache[STRING_CACHE_CAPACITY];
static uint16_t s_request_count[256];
static pending_string_desc_t s_pending_string;
static uint32_t s_string_retry_ms = 0;
static pending_string_request_t s_string_req_queue[STRING_REQ_QUEUE_LEN];
static extra_string_fetch_t s_extra_fetch_queue[EXTRA_FETCH_QUEUE_LEN];
static extra_string_fetch_t* s_extra_fetch_active = NULL;

static uint32_t time_now(void)
{
    return s_ops.time_ms ? s_ops.time_ms() : 0;
}

static bool send_frames(uint8_t cmd, const uint8_t* data, uint16_t len)
{
    return s_ops.send_frames ? s_ops.send_frames(cmd, data, len) : false;
}

static void process_pending_string_descriptor(void);
static void process_pending_string_requests(void);
static void extra_fetch_poll(void);
static uint16_t normalize_string_langid(uint8_t index, uint16_t langid);
static void cache_fallback_string(uint8_t index, uint16_t langid);
static bool send_string_payload(uint8_t index, uint16_t langid,
                                const uint8_t* data, uint16_t len);
static bool send_empty_string_descriptor(uint8_t index, uint16_t langid);
static bool string_cache_send(uint8_t index, uint16_t langid);
static void string_request_complete(uint8_t index, uint16_t langid);
static bool request_extra_string(uint8_t index, uint16_t langid);
static void extra_string_fetch_cb(tuh_xfer_t* xfer);
static bool should_force_fallback(uint8_t index);

void string_manager_init(const string_manager_ops_t* ops)
{
    if (ops)
    {
        s_ops = *ops;
    }
    string_manager_reset();
}

void string_manager_reset(void)
{
    memset(s_string_cache, 0, sizeof(s_string_cache));
    memset(s_pending_string.data, 0, sizeof(s_pending_string.data));
    s_pending_string.pending = false;
    s_pending_string.len     = 0;
    s_pending_string.index   = 0;
    s_pending_string.langid  = 0;
    s_string_retry_ms        = 0;
    memset(s_string_req_queue, 0, sizeof(s_string_req_queue));
    memset(s_extra_fetch_queue, 0, sizeof(s_extra_fetch_queue));
    s_extra_fetch_active = NULL;
    s_default_langid     = 0;
    memset(s_request_count, 0, sizeof(s_request_count));
}

void string_manager_set_default_lang(uint16_t langid)
{
    s_default_langid = langid;
}

uint16_t string_manager_get_default_lang(void)
{
    return s_default_langid;
}

static cached_string_desc_t* string_cache_find(uint8_t index, uint16_t langid)
{
    for (size_t i = 0; i < STRING_CACHE_CAPACITY; ++i)
    {
        cached_string_desc_t* entry = &s_string_cache[i];
        if (entry->valid && entry->index == index && entry->langid == langid)
        {
            return entry;
        }
    }
    return NULL;
}

static cached_string_desc_t* string_cache_alloc(uint8_t index, uint16_t langid)
{
    cached_string_desc_t* entry = string_cache_find(index, langid);
    if (entry)
    {
        return entry;
    }

    for (size_t i = 0; i < STRING_CACHE_CAPACITY; ++i)
    {
        entry = &s_string_cache[i];
        if (!entry->valid)
        {
            entry->index  = index;
            entry->langid = langid;
            return entry;
        }
    }

    entry = &s_string_cache[0];
    LOGW("[B] string cache full, overwriting idx=%u lang=0x%04X",
         entry->index,
         entry->langid);
    entry->index  = index;
    entry->langid = langid;
    entry->valid  = false;
    return entry;
}

void string_manager_cache_store(uint8_t index, uint16_t langid,
                                const uint8_t* data, uint16_t len)
{
    if (!data || !len)
    {
        return;
    }

    if (len > PROXY_STRING_DESC_MAX)
    {
        len = PROXY_STRING_DESC_MAX;
    }

    cached_string_desc_t* entry = string_cache_alloc(index, langid);
    memcpy(entry->data, data, len);
    entry->len   = len;
    entry->valid = true;
    entry->synthetic = false;
    entry->synthetic_sent = false;

    LOGI("[B] cached string idx=%u lang=0x%04X len=%u",
         index,
         langid,
         len);

    if (string_cache_send(index, langid))
    {
        string_request_complete(index, langid);
    }

    process_pending_string_requests();
}

void string_manager_handle_ctrl_request(const uint8_t* payload, uint16_t len)
{
    if (len < 3)
    {
        LOGW("[B] STRING_REQ payload too short len=%u", len);
        uint8_t empty_desc[2] = { 2, TUSB_DESC_STRING };
        send_string_payload(0, 0, empty_desc, sizeof(empty_desc));
        return;
    }

    uint8_t index = payload[0];
    uint16_t requested_lang = (uint16_t)payload[1] | ((uint16_t)payload[2] << 8);
    uint16_t effective_lang = normalize_string_langid(index, requested_lang);

    uint16_t req_count = ++s_request_count[index];
    if (effective_lang != requested_lang)
    {
        LOGI("[B] STRING_REQ received idx=%u lang=0x%04X normalized=0x%04X",
             index,
             requested_lang,
             effective_lang);
    }
    else
    {
        if (req_count <= 5 || (req_count % 10) == 0)
        {
            LOGI("[B] STRING_REQ received idx=%u lang=0x%04X count=%u",
                 index,
                 effective_lang,
                 req_count);
        }
    }

    for (size_t i = 0; i < STRING_REQ_QUEUE_LEN; ++i)
    {
        pending_string_request_t* req = &s_string_req_queue[i];
        if (req->pending &&
            req->index == index &&
            req->langid == effective_lang)
        {
            process_pending_string_requests();
            return;
        }
    }

    for (size_t i = 0; i < STRING_REQ_QUEUE_LEN; ++i)
    {
        pending_string_request_t* req = &s_string_req_queue[i];
        if (!req->pending)
        {
            req->pending        = true;
            req->fetching       = false;
            req->fallback_sent  = false;
            req->fetch_start_ms = 0;
            req->index          = index;
            req->langid         = effective_lang;
            req->retry_count    = 0;
            process_pending_string_requests();
            return;
        }
    }

    LOGW("[B] string request queue full idx=%u lang=0x%04X",
         index,
         effective_lang);
    cache_fallback_string(index, effective_lang);
}

void string_manager_task(void)
{
    process_pending_string_descriptor();
    process_pending_string_requests();
    extra_fetch_poll();
}

static bool send_string_payload(uint8_t index, uint16_t langid,
                                const uint8_t* data, uint16_t len)
{
    // Do not forward empty or synthetic data when fallback is disabled.
    if (!data || !len)
    {
        return false;
    }

    if (len > PROXY_STRING_DESC_MAX)
    {
        len = PROXY_STRING_DESC_MAX;
    }

    uint16_t payload_len = len + 1;
    if (payload_len > PROTO_MAX_PAYLOAD_SIZE)
    {
        len = PROTO_MAX_PAYLOAD_SIZE - 1;
        payload_len = len + 1;
    }

    uint8_t payload[PROTO_MAX_PAYLOAD_SIZE];
    payload[0] = index;
    memcpy(&payload[1], data, len);

    if (!send_frames(PF_DESC_STRING, payload, payload_len))
    {
        memcpy(s_pending_string.data, data, len);
        s_pending_string.index  = index;
        s_pending_string.langid = langid;
        s_pending_string.len    = len;
        s_pending_string.pending = true;
        s_string_retry_ms = time_now();
        LOGW("[B] failed to forward string descriptor idx=%u, will retry", index);
        return false;
    }

    LOGI("[B] string descriptor forwarded idx=%u len=%u", index, len);
    string_request_complete(index, langid);
    process_pending_string_requests();

    return true;
}

static bool send_empty_string_descriptor(uint8_t index, uint16_t langid)
{
    uint8_t empty_desc[2] = { 2, TUSB_DESC_STRING };
    return send_string_payload(index, langid, empty_desc, sizeof(empty_desc));
}

static bool string_cache_send(uint8_t index, uint16_t langid)
{
    // Try exact lang match first.
    cached_string_desc_t* entry = string_cache_find(index, langid);
    // If not found, fall back to any cached entry for this index.
    if (!entry)
    {
        for (size_t i = 0; i < STRING_CACHE_CAPACITY; ++i)
        {
            if (s_string_cache[i].valid && s_string_cache[i].index == index)
            {
                entry = &s_string_cache[i];
                break;
            }
        }
    }

    if (!entry || !entry->valid)
    {
        return false;
    }

    bool ok = send_string_payload(index, entry->langid, entry->data, entry->len);
    return ok;
}

static void process_pending_string_descriptor(void)
{
    if (!s_pending_string.pending)
    {
        return;
    }

    if (s_string_retry_ms)
    {
        uint32_t now = time_now();
        if ((now - s_string_retry_ms) < 5)
        {
            return;
        }
        s_string_retry_ms = 0;
    }

    uint16_t payload_len = s_pending_string.len + 1;
    if (payload_len > PROTO_MAX_PAYLOAD_SIZE)
    {
        payload_len = PROTO_MAX_PAYLOAD_SIZE;
    }

    uint8_t payload[PROTO_MAX_PAYLOAD_SIZE];
    payload[0] = s_pending_string.index;
    memcpy(&payload[1], s_pending_string.data, payload_len - 1);

    LOGI("[B] processing pending string idx=%u len=%u",
         s_pending_string.index, s_pending_string.len);

    if (!send_frames(PF_DESC_STRING, payload, payload_len))
    {
        LOGW("[B] failed to forward string descriptor idx=%u", payload[0]);
        s_string_retry_ms = time_now();
        return;
    }

    LOGI("[B] string descriptor forwarded idx=%u len=%u",
         payload[0], payload_len - 1);

    string_request_complete(s_pending_string.index, s_pending_string.langid);
    s_pending_string.pending = false;
}

static pending_string_request_t* string_request_find(uint8_t index,
                                                     uint16_t langid)
{
    for (size_t i = 0; i < STRING_REQ_QUEUE_LEN; ++i)
    {
        pending_string_request_t* req = &s_string_req_queue[i];
        if (req->pending &&
            req->index == index &&
            req->langid == langid)
        {
            return req;
        }
    }
    return NULL;
}

static void string_request_complete(uint8_t index, uint16_t langid)
{
    pending_string_request_t* req = string_request_find(index, langid);
    if (req)
    {
        req->pending        = false;
        req->fetching       = false;
        req->fallback_sent  = false;
        req->fetch_start_ms = 0;
        req->retry_count    = 0;
    }
}

static extra_string_fetch_t* extra_fetch_find(uint8_t index, uint16_t langid)
{
    for (size_t i = 0; i < EXTRA_FETCH_QUEUE_LEN; ++i)
    {
        extra_string_fetch_t* entry = &s_extra_fetch_queue[i];
        if (entry->queued &&
            entry->index == index &&
            entry->langid == langid)
        {
            return entry;
        }
    }

    return NULL;
}

static extra_string_fetch_t* extra_fetch_alloc_slot(uint8_t index,
                                                    uint16_t langid)
{
    for (size_t i = 0; i < EXTRA_FETCH_QUEUE_LEN; ++i)
    {
        extra_string_fetch_t* entry = &s_extra_fetch_queue[i];
        if (!entry->queued)
        {
            entry->queued   = true;
            entry->active   = false;
            entry->index    = index;
            entry->langid   = langid;
            entry->start_ms = 0;
            return entry;
        }
    }

    return NULL;
}

static void extra_fetch_release(extra_string_fetch_t* entry)
{
    if (entry)
    {
        entry->queued   = false;
        entry->active   = false;
        entry->start_ms = 0;
        if (s_extra_fetch_active == entry)
        {
            s_extra_fetch_active = NULL;
        }
    }
}

static void extra_fetch_start(void)
{
    if (s_extra_fetch_active)
    {
        return;
    }

    for (size_t i = 0; i < EXTRA_FETCH_QUEUE_LEN; ++i)
    {
        extra_string_fetch_t* entry = &s_extra_fetch_queue[i];
        if (entry->queued && !entry->active)
        {
            uint8_t dev_addr = hid_proxy_host_first_dev_addr();
            if (!dev_addr)
            {
                extra_fetch_release(entry);
                continue;
            }

            if (!tuh_descriptor_get_string(dev_addr,
                                           entry->index,
                                           entry->langid,
                                           entry->buffer,
                                           sizeof(entry->buffer),
                                           extra_string_fetch_cb,
                                           (uintptr_t)entry))
            {
                LOGW("[B] failed to request extra string idx=%u", entry->index);
                extra_fetch_release(entry);
                cache_fallback_string(entry->index, entry->langid);
                continue;
            }

            entry->active   = true;
            entry->start_ms = time_now();
            s_extra_fetch_active = entry;
            LOGI("[B] requesting extra string idx=%u lang=0x%04X from device",
                 entry->index,
                 entry->langid);
            break;
        }
    }
}

static void extra_fetch_poll(void)
{
    extra_string_fetch_t* entry = s_extra_fetch_active;
    if (!entry || !entry->active)
    {
        return;
    }

    uint32_t now = time_now();
    if (entry->start_ms == 0)
    {
        entry->start_ms = now;
        return;
    }

    if ((now - entry->start_ms) < EXTRA_FETCH_TIMEOUT_MS)
    {
        return;
    }

    LOGW("[B] extra string idx=%u lang=0x%04X timed out, using fallback",
         entry->index,
         entry->langid);
    cache_fallback_string(entry->index, entry->langid);
    if (!STRING_FALLBACK_ENABLED)
    {
        send_empty_string_descriptor(entry->index, entry->langid);
        string_request_complete(entry->index, entry->langid);
    }
    extra_fetch_release(entry);
    extra_fetch_start();
    process_pending_string_requests();
}

static bool request_extra_string(uint8_t index, uint16_t langid)
{
    uint8_t dev_addr = hid_proxy_host_first_dev_addr();
    if (!dev_addr)
    {
        return false;
    }

    if (extra_fetch_find(index, langid))
    {
        return true;
    }

    extra_string_fetch_t* entry = extra_fetch_alloc_slot(index, langid);
    if (!entry)
    {
        LOGW("[B] extra string queue full idx=%u lang=0x%04X", index, langid);
        return false;
    }

    extra_fetch_start();
    return true;
}

static void extra_string_fetch_cb(tuh_xfer_t* xfer)
{
    extra_string_fetch_t* entry = (extra_string_fetch_t*)(uintptr_t)xfer->user_data;
    if (!entry || !entry->queued || xfer->daddr != hid_proxy_host_first_dev_addr())
    {
        return;
    }

    uint8_t index  = entry->index;
    uint16_t lang  = entry->langid;
    bool success = (xfer->result == XFER_RESULT_SUCCESS);
    uint16_t len = success
                   ? (uint16_t)TU_MIN((size_t)xfer->actual_len,
                                      sizeof(entry->buffer))
                   : 0;

    if (success && len)
    {
        string_manager_cache_store(index, lang, entry->buffer, len);
        LOGI("[B] extra string idx=%u lang=0x%04X loaded", index, lang);
    }
    else
    {
        LOGW("[B] extra string request failed idx=%u result=%d",
             index,
             xfer->result);
        cache_fallback_string(index, lang);
        if (!STRING_FALLBACK_ENABLED)
        {
            send_empty_string_descriptor(index, lang);
            string_request_complete(index, lang);
        }
    }

    extra_fetch_release(entry);
    extra_fetch_start();
    process_pending_string_requests();
}

static void process_pending_string_requests(void)
{
    for (size_t i = 0; i < STRING_REQ_QUEUE_LEN; ++i)
    {
        pending_string_request_t* req = &s_string_req_queue[i];
        if (!req->pending)
        {
            continue;
        }

        if (string_cache_send(req->index, req->langid))
        {
            string_request_complete(req->index, req->langid);
            continue;
        }

        if (!req->fetching)
        {
            if (request_extra_string(req->index, req->langid))
            {
                req->fetching       = true;
                req->fetch_start_ms = time_now();
            }
            else
            {
                // Не змогли поставити запит: рахуємо спробу і при ліміті даємо порожній дескриптор.
                req->retry_count++;
                if (req->retry_count >= STRING_FETCH_MAX_RETRIES)
                {
                    if (!STRING_FALLBACK_ENABLED)
                    {
                        send_empty_string_descriptor(req->index, req->langid);
                    }
                    string_request_complete(req->index, req->langid);
                }
            }
        }

        if (req->fetching &&
            !req->fallback_sent &&
            req->fetch_start_ms &&
            (time_now() - req->fetch_start_ms) >= STRING_REQ_FALLBACK_MS)
        {
            LOGW("[B] string idx=%u lang=0x%04X fetch timeout, using fallback",
                 req->index,
                 req->langid);
            req->fetching = false;
            req->fetch_start_ms = 0;
            req->retry_count++;

            bool retries_left = (req->retry_count < STRING_FETCH_MAX_RETRIES);

            if (retries_left)
            {
                // Try again; will be re-armed on next loop iteration.
                LOGI("[B] retrying string idx=%u lang=0x%04X attempt=%u",
                     req->index,
                     req->langid,
                     req->retry_count);
            }
            else
            {
                cache_fallback_string(req->index, req->langid);
                if (!STRING_FALLBACK_ENABLED)
                {
                    send_empty_string_descriptor(req->index, req->langid);
                    string_request_complete(req->index, req->langid);
                }
                req->fallback_sent = true;
            }
        }
    }
}

static bool should_force_fallback(uint8_t index)
{
    (void)index;
    return STRING_FALLBACK_ENABLED;
}

static uint16_t normalize_string_langid(uint8_t index, uint16_t langid)
{
    if (index == 0 || langid != 0)
    {
        return langid;
    }

    if (s_default_langid)
    {
        return s_default_langid;
    }

    for (size_t i = 0; i < STRING_CACHE_CAPACITY; ++i)
    {
        cached_string_desc_t* entry = &s_string_cache[i];
        if (entry->valid &&
            entry->index == index &&
            entry->langid != 0)
        {
            return entry->langid;
        }
    }

    return 0x0409;
}

static void cache_fallback_string(uint8_t index, uint16_t langid)
{
    // Fallback injection disabled: do nothing to avoid synthetic descriptors.
    (void)index;
    (void)langid;
    return;

    char ascii[24];
    int written = snprintf(ascii, sizeof(ascii), "IDX%u", index);
    if (written <= 0)
    {
        written = 1;
        ascii[0] = '?';
    }

    // For idx0 keep the requested langid (host expects 0x0000). For others pick a sane default.
    if (langid == 0 && index != 0)
    {
        langid = s_default_langid ? s_default_langid : 0x0409;
    }

    uint8_t buffer[PROXY_STRING_DESC_MAX];
    uint16_t byte_len;

    uint16_t max_chars = (uint16_t)((PROXY_STRING_DESC_MAX - 2) / 2);
    uint16_t chars = (uint16_t)written;
    if (chars > max_chars)
    {
        chars = max_chars;
    }

    byte_len = (uint16_t)(2 + chars * 2);
    buffer[0] = (uint8_t)byte_len;
    buffer[1] = TUSB_DESC_STRING;
    for (uint16_t i = 0; i < chars; ++i)
    {
        buffer[2 + i * 2] = (uint8_t)ascii[i];
        buffer[3 + i * 2] = 0;
    }

    string_manager_cache_store(index, langid, buffer, byte_len);
}
