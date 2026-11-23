// proxy28b.c — 完全接管 Cysharp.Net.Http.YetAnotherHttpHandler 传输层
//
// 权威导出表（从 orig DLL 的 PE 导出表实测，共 45 个）与权威签名（上游 NativeMethods.g.cs）：
//   void  yaha_init_runtime(void)
//   void  yaha_dispose_runtime(rt)
//   ctx*  yaha_init_context(rt, on_status, on_receive, on_complete)
//   void  yaha_dispose_context(ctx)
//   void  yaha_build_client(ctx)                       // ★ 只有 1 个参数、void 返回
//   req*  yaha_request_new(ctx, seq)
//   bool  yaha_request_set_method(ctx, req, StringBuffer*)
//   bool  yaha_request_set_uri(ctx, req, StringBuffer*)
//   bool  yaha_request_set_header(ctx, req, StringBuffer* k, StringBuffer* v)
//   bool  yaha_request_set_version(ctx, req, YahaHttpVersion)
//   bool  yaha_request_set_has_body(ctx, req, bool)
//   bool  yaha_request_begin(ctx, req, nint state)
//   WriteResult yaha_request_write_body(ctx, req, byte*, nuint)
//   bool  yaha_request_complete_body(ctx, req)
//   void  yaha_request_abort(ctx, req)
//   void  yaha_request_destroy(ctx, req)
//   int   yaha_request_response_get_headers_count(ctx, req)
//   ByteBuffer* yaha_request_response_get_header_key / _value(ctx, req, int)
//   int   yaha_request_response_get_trailers_count(ctx, req)
//   ByteBuffer* yaha_request_response_get_trailers_key / _value(ctx, req, int)
//   ByteBuffer* yaha_get_last_error(ctx, req)
//   void  yaha_free_byte_buffer(ByteBuffer*)
//   void  yaha_complete_task(nuint task_handle, StringBuffer* error)
//   yaha_client_config_*：全部 void，第一个参数是 ctx（★ 无 set_ 前缀，config 内嵌在 ctx）
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>

#pragma comment(lib, "ws2_32.lib")

/* ---------------- 日志 ---------------- */
static char g_logpath[MAX_PATH];
static CRITICAL_SECTION g_logcs;
static volatile LONG g_logstate = 0;

static void loginit(void)
{
    if (InterlockedCompareExchange(&g_logstate, 1, 0) != 0) {
        while (g_logstate != 2) Sleep(1);
        return;
    }
    InitializeCriticalSection(&g_logcs);
    if (!g_logpath[0]) {
        HMODULE hm = NULL;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCSTR)&loginit, &hm);
        GetModuleFileNameA(hm, g_logpath, MAX_PATH);
        char *p = strrchr(g_logpath, '\\');
        if (p) *p = 0;
        strncat(g_logpath, "\\yaha-proxy.log", sizeof(g_logpath) - strlen(g_logpath) - 2);
    }
    InterlockedExchange(&g_logstate, 2);
}

static void lg(const char *fmt, ...)
{
    loginit();
    EnterCriticalSection(&g_logcs);
    FILE *f = fopen(g_logpath, "a");
    if (f) {
        SYSTEMTIME st; GetLocalTime(&st);
        fprintf(f, "[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap);
        fputc('\n', f); fclose(f);
    }
    LeaveCriticalSection(&g_logcs);
}

/* ---------------- 结构 ---------------- */
typedef struct { const uint8_t *ptr; int32_t length; } StrBuf;
typedef struct { uint8_t *ptr; int32_t length, capacity; } ByteBuf;

typedef void (*cb_status)(int req_seq, intptr_t state, int status_code, int version);
typedef void (*cb_recv)(int req_seq, intptr_t state, size_t length, uint8_t *buf, size_t task_handle);
typedef void (*cb_done)(int req_seq, intptr_t state, int reason, unsigned int h2err);

typedef struct {
    int seq;
    intptr_t state;
    int has_body, done;
    char *method, *uri;
    char **hk, **hv; int hn, hcap;
    unsigned char *body; size_t blen, bcap;
    int rcode;
    char **rhk, **rhv; int rhn, rhcap;
    char **rtk, **rtv; int rtn, rtcap;
    char slotk[512], slotv[8192];
    ByteBuf bk, bv;
    CRITICAL_SECTION cs;
} Req;

typedef struct { intptr_t runtime; cb_status on_status; cb_recv on_recv; cb_done on_done; } Ctx;

static char g_err[512];
static ByteBuf g_errbuf;

static char *dupstr(const char *s, int n)
{
    char *d = (char *)malloc((size_t)(n > 0 ? n : 0) + 1);
    if (!d) return NULL;
    if (n > 0 && s) memcpy(d, s, (size_t)n);
    d[n > 0 ? n : 0] = 0;
    return d;
}

/* ---------------- 生命周期 ---------------- */
__declspec(dllexport) void yaha_init_runtime(void) { lg("RUNTIME init"); }
__declspec(dllexport) void yaha_dispose_runtime(void *rt) { (void)rt; }

__declspec(dllexport) void *yaha_init_context(void *runtime, cb_status s, cb_recv r, cb_done c)
{
    loginit();
    lg("CONTEXT status=%p recv=%p done=%p", (void *)s, (void *)r, (void *)c);
    Ctx *x = (Ctx *)calloc(1, sizeof(Ctx));
    if (!x) return NULL;
    x->runtime = (intptr_t)runtime; x->on_status = s; x->on_recv = r; x->on_done = c;
    return x;
}

__declspec(dllexport) void yaha_dispose_context(void *ctx) { lg("DISPOSE ctx=%p", ctx); }

__declspec(dllexport) void yaha_build_client(void *ctx)
{
    loginit();
    lg("BUILD_CLIENT ctx=%p (transport handled by hook)", ctx);
}

/* ---------------- 请求 ---------------- */
__declspec(dllexport) void *yaha_request_new(void *ctx, int seq)
{
    Req *r = (Req *)calloc(1, sizeof(Req));
    if (!r) return NULL;
    r->seq = seq; r->rcode = 200;
    InitializeCriticalSection(&r->cs);
    r->bk.ptr = (uint8_t *)r->slotk; r->bk.capacity = sizeof(r->slotk);
    r->bv.ptr = (uint8_t *)r->slotv; r->bv.capacity = sizeof(r->slotv);
    lg("NEW seq=%d req=%p", seq, (void *)r);
    return r;
}

__declspec(dllexport) int yaha_request_set_method(void *ctx, Req *r, StrBuf *m)
{
    if (!r) return 0;
    if (m && m->ptr && m->length > 0) r->method = dupstr((const char *)m->ptr, m->length);
    if (!r->method) r->method = dupstr("POST", 4);
    lg("  METHOD seq=%d = %s", r->seq, r->method);
    return 1;
}

__declspec(dllexport) int yaha_request_set_version(void *ctx, Req *r, int v) { (void)ctx; (void)r; (void)v; return 1; }

__declspec(dllexport) int yaha_request_set_uri(void *ctx, Req *r, StrBuf *u)
{
    if (!r) return 0;
    if (u && u->ptr && u->length > 0) r->uri = dupstr((const char *)u->ptr, u->length);
    lg("  URI seq=%d = %s", r->seq, r->uri ? r->uri : "(null)");
    return 1;
}

__declspec(dllexport) int yaha_request_set_header(void *ctx, Req *r, StrBuf *k, StrBuf *v)
{
    if (!r) return 0;
    if (!k || !k->ptr || k->length <= 0) return 1;
    const char *vp = (v && v->ptr) ? (const char *)v->ptr : "";
    int vlen = (v && v->ptr) ? v->length : 0;

    EnterCriticalSection(&r->cs);
    if (r->hn + 1 > r->hcap) {
        int nc = r->hcap ? r->hcap * 2 : 16;
        char **nk = (char **)realloc(r->hk, sizeof(char *) * (size_t)nc);
        char **nv = (char **)realloc(r->hv, sizeof(char *) * (size_t)nc);
        if (!nk || !nv) { LeaveCriticalSection(&r->cs); return 0; }
        r->hk = nk; r->hv = nv; r->hcap = nc;
    }
    char *ks = dupstr((const char *)k->ptr, k->length);
    char *vs = dupstr(vp, vlen);
    if (!ks || !vs) { free(ks); free(vs); LeaveCriticalSection(&r->cs); return 0; }
    r->hk[r->hn] = ks; r->hv[r->hn] = vs; r->hn++;
    LeaveCriticalSection(&r->cs);
    lg("  HDR seq=%d %s: %s", r->seq, ks, vs);
    return 1;
}

__declspec(dllexport) int yaha_request_set_has_body(void *ctx, Req *r, int b)
{
    if (!r) return 0;
    r->has_body = b ? 1 : 0;
    lg("  HAS_BODY seq=%d = %d", r->seq, r->has_body);
    return 1;
}

__declspec(dllexport) int yaha_request_write_body(void *ctx, Req *r, uint8_t *buf, size_t len)
{
    if (!r) return 2;
    if (len == 0) return 0;
    EnterCriticalSection(&r->cs);
    if (r->blen + len > r->bcap) {
        size_t nc = r->bcap ? r->bcap : 65536;
        while (nc < r->blen + len) nc *= 2;
        unsigned char *nb = (unsigned char *)realloc(r->body, nc);
        if (!nb) { LeaveCriticalSection(&r->cs); return 1; }
        r->body = nb; r->bcap = nc;
    }
    memcpy(r->body + r->blen, buf, len);
    r->blen += len;
    LeaveCriticalSection(&r->cs);
    return 0; /* WriteResult.Success */
}

/* ---------------- 传输 ---------------- */
static const char *uri_path(const char *uri)
{
    if (!uri) return "/";
    const char *p = strstr(uri, "://");
    if (p) { p = strchr(p + 3, '/'); return p ? p : "/"; }
    return uri;
}

static int send_all(SOCKET s, const char *b, int n)
{
    int off = 0;
    while (off < n) { int k = send(s, b + off, n - off, 0); if (k <= 0) return -1; off += k; }
    return 0;
}

static void add_kv(char ***ka, char ***va, int *n, int *cap, const char *k, int klen, const char *v, int vlen)
{
    if (*n + 1 > *cap) {
        int nc = *cap ? *cap * 2 : 16;
        char **nk = (char **)realloc(*ka, sizeof(char *) * (size_t)nc);
        char **nv = (char **)realloc(*va, sizeof(char *) * (size_t)nc);
        if (!nk || !nv) return;
        *ka = nk; *va = nv; *cap = nc;
    }
    (*ka)[*n] = dupstr(k, klen);
    (*va)[*n] = dupstr(v, vlen);
    (*n)++;
}

static void parse_kv(char *p, char *end, char ***ka, char ***va, int *n, int *cap)
{
    while (p < end) {
        char *eol = p;
        while (eol < end && !(eol[0] == '\r' && eol + 1 < end && eol[1] == '\n')) eol++;
        if (eol == p) break;
        char *c = p;
        while (c < eol && *c != ':') c++;
        if (c < eol) {
            int klen = (int)(c - p);
            char *v = c + 1;
            while (v < eol && (*v == ' ' || *v == '\t')) v++;
            add_kv(ka, va, n, cap, p, klen, v, (int)(eol - v));
        }
        p = eol + 2;
    }
}

static void call_reply(Ctx *c, Req *r, int code, const char *body, int blen)
{
    if (c->on_status) c->on_status(r->seq, r->state, code, 3 /* Http2 */);
    if (c->on_recv && blen > 0) c->on_recv(r->seq, r->state, (size_t)blen, (uint8_t *)body, 0);
    if (!r->done) { r->done = 1; if (c->on_done) c->on_done(r->seq, r->state, 0 /* Success */, 0); }
}

static DWORD WINAPI worker(LPVOID param)
{
    void **a = (void **)param;
    Ctx *c = (Ctx *)a[0]; Req *r = (Req *)a[1];
    free(param);

    const char *path = uri_path(r->uri);
    lg("TRANSMIT seq=%d %s %s bodylen=%d", r->seq, r->method ? r->method : "POST", path, (int)r->blen);

    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) { call_reply(c, r, 0, NULL, 0); return 1; }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) { WSACleanup(); call_reply(c, r, 0, NULL, 0); return 1; }

    struct sockaddr_in sa; memset(&sa, 0, sizeof(sa));
    sa.sin_family = AF_INET; sa.sin_port = htons(18444); sa.sin_addr.s_addr = inet_addr("127.0.0.1");
    if (connect(s, (struct sockaddr *)&sa, sizeof(sa)) != 0) {
        lg("CONNECT FAIL seq=%d", r->seq);
        closesocket(s); WSACleanup(); call_reply(c, r, 0, NULL, 0); return 1;
    }

    char *hdr = (char *)malloc(32768);
    if (!hdr) { closesocket(s); WSACleanup(); call_reply(c, r, 0, NULL, 0); return 1; }
    int n = _snprintf(hdr, 32000, "%s %s HTTP/1.1\r\nHost: api.game-gakuen-idolmaster.jp\r\n",
                      r->method ? r->method : "POST", path);
    EnterCriticalSection(&r->cs);
    for (int i = 0; i < r->hn && n < 30000; i++)
        n += _snprintf(hdr + n, 32000 - n, "%s: %s\r\n", r->hk[i], r->hv[i]);
    LeaveCriticalSection(&r->cs);
    n += _snprintf(hdr + n, 32000 - n, "Content-Length: %d\r\nConnection: close\r\n\r\n", (int)r->blen);
    send_all(s, hdr, n);
    free(hdr);
    if (r->blen > 0) send_all(s, (const char *)r->body, (int)r->blen);

    char *raw = (char *)malloc(8 * 1024 * 1024);
    int rawlen = 0;
    if (raw) {
        while (rawlen < 8 * 1024 * 1024 - 2) {
            int k = recv(s, raw + rawlen, 8 * 1024 * 1024 - 1 - rawlen, 0);
            if (k <= 0) break;
            rawlen += k;
        }
        raw[rawlen] = 0;
    }
    closesocket(s); WSACleanup();

    if (!raw || rawlen == 0) { lg("RESP EMPTY seq=%d", r->seq); free(raw); call_reply(c, r, 0, NULL, 0); return 1; }

    char *hend = strstr(raw, "\r\n\r\n");
    int code = 0;
    sscanf(raw, "HTTP/%*d.%*d %d", &code);
    lg("RESP seq=%d code=%d rawlen=%d", r->seq, code, rawlen);
    if (!hend) { call_reply(c, r, code ? code : 200, raw, rawlen); free(raw); return 0; }

    char *hstart = strchr(raw, '\r');
    if (hstart) parse_kv(hstart + 2, hend, &r->rhk, &r->rhv, &r->rhn, &r->rhcap);

    char *b = hend + 4;
    int bl = rawlen - (int)(b - raw);
    char *dec = (char *)malloc((size_t)bl + 1);
    int dl = 0;
    if (dec) {
        int chunked = 0;
        for (int i = 0; i < r->rhn; i++)
            if (_stricmp(r->rhk[i], "transfer-encoding") == 0 &&
                _strnicmp(r->rhv[i], "chunked", 7) == 0) chunked = 1;

        if (chunked) {
            char *p = b, *limit = raw + rawlen;
            while (p < limit) {
                char *nl = p;
                while (nl < limit && *nl != '\r') nl++;
                long sz = strtol(p, NULL, 16);
                if (sz <= 0) { p = nl + 2; break; }
                p = nl + 2;
                if (p + sz > limit) sz = limit - p;
                memcpy(dec + dl, p, (size_t)sz); dl += (int)sz;
                p += sz + 2;
            }
            if (p < limit) parse_kv(p, limit, &r->rtk, &r->rtv, &r->rtn, &r->rtcap);
        } else {
            memcpy(dec, b, (size_t)bl); dl = bl;
        }
    }

    int has_grpc_status = 0;
    for (int i = 0; i < r->rtn; i++)
        if (_stricmp(r->rtk[i], "grpc-status") == 0) has_grpc_status = 1;
    if (!has_grpc_status) add_kv(&r->rtk, &r->rtv, &r->rtn, &r->rtcap, "grpc-status", 11, "0", 1);

    r->rcode = code ? code : 200;
    call_reply(c, r, r->rcode, dec ? dec : b, dec ? dl : bl);
    free(dec); free(raw);
    return 0;
}

__declspec(dllexport) int yaha_request_begin(void *ctx, Req *r, intptr_t state)
{
    if (!r) return 0;
    r->state = state;
    lg("BEGIN seq=%d state=0x%llX has_body=%d", r->seq, (unsigned long long)state, r->has_body);
    return 1;
}

__declspec(dllexport) int yaha_request_complete_body(void *ctx, Req *r)
{
    if (!r) return 0;
    lg("COMPLETE_BODY seq=%d blen=%d", r->seq, (int)r->blen);
    void **a = (void **)malloc(sizeof(void *) * 2);
    if (!a) return 0;
    a[0] = ctx; a[1] = r;
    HANDLE th = CreateThread(NULL, 0, worker, a, 0, NULL);
    if (th) CloseHandle(th);
    return 1;
}

__declspec(dllexport) void yaha_request_abort(void *ctx, Req *r) { lg("ABORT seq=%d", r ? r->seq : -1); }

__declspec(dllexport) void yaha_request_destroy(void *ctx, Req *r)
{
    if (!r) return;
    lg("DESTROY seq=%d uri=%s", r->seq, r->uri ? r->uri : "-");
    EnterCriticalSection(&r->cs);
    for (int i = 0; i < r->hn; i++) { free(r->hk[i]); free(r->hv[i]); }
    free(r->hk); free(r->hv);
    LeaveCriticalSection(&r->cs);
    for (int i = 0; i < r->rhn; i++) { free(r->rhk[i]); free(r->rhv[i]); }
    free(r->rhk); free(r->rhv);
    for (int i = 0; i < r->rtn; i++) { free(r->rtk[i]); free(r->rtv[i]); }
    free(r->rtk); free(r->rtv);
    free(r->method); free(r->uri); free(r->body);
    DeleteCriticalSection(&r->cs);
    free(r);
}

__declspec(dllexport) void yaha_complete_task(size_t task, StrBuf *err) { (void)task; (void)err; }

/* ---------------- 响应头/尾 ---------------- */
__declspec(dllexport) int yaha_request_response_get_headers_count(void *ctx, Req *r) { return r ? r->rhn : 0; }
__declspec(dllexport) int yaha_request_response_get_trailers_count(void *ctx, Req *r) { return r ? r->rtn : 0; }

/* key / value 必须各自独立的 ByteBuffer 结构体：
   C# 连续调 get_header_key(i) / get_header_value(i) 拿到两个指针后，
   再分别 ->AsSpan()。若两个函数返回同一个 &r->bv，第二次调用会改掉
   bufKey->ptr/length，key 会变成 value 的内容。 */
static ByteBuf *fill_slot(Req *r, char **arr, int idx, int is_key)
{
    const char *s = (arr && idx >= 0 && arr[idx]) ? arr[idx] : "";
    size_t n = strlen(s);
    char *dst = is_key ? r->slotk : r->slotv;
    size_t cap = is_key ? sizeof(r->slotk) : sizeof(r->slotv);
    if (n > cap - 1) n = cap - 1;
    memcpy(dst, s, n); dst[n] = 0;
    ByteBuf *bb = is_key ? &r->bk : &r->bv;
    bb->ptr = (uint8_t *)dst; bb->length = (int32_t)n; bb->capacity = (int32_t)cap;
    return bb;
}

__declspec(dllexport) ByteBuf *yaha_request_response_get_header_key(void *ctx, Req *r, int i) { return r ? fill_slot(r, r->rhk, i, 1) : NULL; }
__declspec(dllexport) ByteBuf *yaha_request_response_get_header_value(void *ctx, Req *r, int i) { return r ? fill_slot(r, r->rhv, i, 0) : NULL; }
__declspec(dllexport) ByteBuf *yaha_request_response_get_trailers_key(void *ctx, Req *r, int i) { return r ? fill_slot(r, r->rtk, i, 1) : NULL; }
__declspec(dllexport) ByteBuf *yaha_request_response_get_trailers_value(void *ctx, Req *r, int i) { return r ? fill_slot(r, r->rtv, i, 0) : NULL; }

__declspec(dllexport) ByteBuf *yaha_get_last_error(void *ctx, Req *r)
{
    snprintf(g_err, sizeof(g_err), "proxy28: seq=%d uri=%s", r ? r->seq : -1, (r && r->uri) ? r->uri : "-");
    g_errbuf.ptr = (uint8_t *)g_err; g_errbuf.length = (int32_t)strlen(g_err); g_errbuf.capacity = (int32_t)sizeof(g_err);
    return &g_errbuf;
}

__declspec(dllexport) void yaha_free_byte_buffer(ByteBuf *b) { (void)b; }

/* ---------------- client_config（全部 void，第一个参数 = ctx） ---------------- */
#define CFG_BOOL(name) __declspec(dllexport) void name(void *ctx, int v) { (void)ctx; (void)v; }
#define CFG_INT(name)  __declspec(dllexport) void name(void *ctx, int v) { (void)ctx; (void)v; }
#define CFG_DUR(name)  __declspec(dllexport) void name(void *ctx, int64_t v) { (void)ctx; (void)v; }
#define CFG_STR(name)  __declspec(dllexport) void name(void *ctx, StrBuf *v) { (void)ctx; (void)v; }

CFG_BOOL(yaha_client_config_skip_certificate_verification)
CFG_BOOL(yaha_client_config_http2_only)
CFG_BOOL(yaha_client_config_http2_adaptive_window)
CFG_BOOL(yaha_client_config_http2_keep_alive_while_idle)
CFG_INT(yaha_client_config_http2_initial_stream_window_size)
CFG_INT(yaha_client_config_http2_initial_connection_window_size)
CFG_INT(yaha_client_config_http2_initial_max_send_streams)
CFG_INT(yaha_client_config_http2_max_frame_size)
CFG_INT(yaha_client_config_http2_max_concurrent_reset_streams)
CFG_INT(yaha_client_config_http2_max_send_buf_size)
CFG_INT(yaha_client_config_pool_max_idle_per_host)
CFG_DUR(yaha_client_config_connect_timeout)
CFG_DUR(yaha_client_config_http2_keep_alive_interval)
CFG_DUR(yaha_client_config_http2_keep_alive_timeout)
CFG_DUR(yaha_client_config_pool_idle_timeout)
CFG_STR(yaha_client_config_add_root_certificates)
CFG_STR(yaha_client_config_add_override_server_name)
CFG_STR(yaha_client_config_add_client_auth_certificates)
CFG_STR(yaha_client_config_add_client_auth_key)

__declspec(dllexport) void yaha_client_config_set_server_certificate_verification_handler(void *ctx, void *h, intptr_t s)
{
    (void)ctx; (void)h; (void)s;
    lg("CFG verify handler (ignored)");
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD r, LPVOID p)
{
    if (r == DLL_PROCESS_ATTACH) { loginit(); lg("=== proxy28b loaded ==="); }
    return TRUE;
}
