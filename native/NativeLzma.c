#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include "LzmaEnc.h"
#include "Alloc.h"

typedef int (__cdecl *ProgressCallback)(uint64_t, uint64_t);
typedef struct { ISeqInStream vt; HANDLE file; uint64_t remaining; } Input;
typedef struct { ISeqOutStream vt; HANDLE file; int failed; } Output;
typedef struct { ICompressProgress vt; ProgressCallback callback; } Progress;

static SRes read_input(ISeqInStreamPtr vt, void *buffer, size_t *size) {
    Input *in = (Input *)vt;
    DWORD got = 0;
    DWORD wanted = (DWORD)(*size > 0x40000000u ? 0x40000000u : *size);
    if (wanted > in->remaining) wanted = (DWORD)in->remaining;
    *size = 0;
    if (!wanted) return SZ_OK;
    if (!ReadFile(in->file, buffer, wanted, &got, NULL)) return SZ_ERROR_READ;
    if (!got) return SZ_ERROR_INPUT_EOF;
    in->remaining -= got;
    *size = got;
    return SZ_OK;
}

static size_t write_output(ISeqOutStreamPtr vt, const void *buffer, size_t size) {
    Output *out = (Output *)vt;
    size_t done = 0;
    while (done < size) {
        DWORD wrote = 0;
        DWORD wanted = (DWORD)(size - done > 0x40000000u ? 0x40000000u : size - done);
        if (!WriteFile(out->file, (const Byte *)buffer + done, wanted, &wrote, NULL) || !wrote) {
            out->failed = 1;
            break;
        }
        done += wrote;
    }
    return done;
}

static SRes report_progress(ICompressProgressPtr vt, UInt64 in, UInt64 out) {
    Progress *p = (Progress *)vt;
    return p->callback && p->callback(in, out) ? SZ_ERROR_PROGRESS : SZ_OK;
}

__declspec(dllexport) int __cdecl nlc_encode(
    const wchar_t *source, uint64_t offset, uint64_t length,
    const wchar_t *destination, int threads, uint32_t dictionary, int fast_bytes,
    ProgressCallback callback) {
    Input in = {{read_input}, INVALID_HANDLE_VALUE, length};
    Output out = {{write_output}, INVALID_HANDLE_VALUE, 0};
    Progress progress = {{report_progress}, callback};
    LARGE_INTEGER size, seek;
    CLzmaEncHandle encoder = NULL;
    CLzmaEncProps props;
    Byte properties[LZMA_PROPS_SIZE];
    SizeT properties_size = LZMA_PROPS_SIZE;
    int result = SZ_ERROR_PARAM;
    if (!source || !destination || !length || length > 0xffffffffu ||
        offset > INT64_MAX || (threads != 1 && threads != 2) ||
        dictionary < 4096 || dictionary > (1u << 27) || fast_bytes < 5 || fast_bytes > 273)
        return result;
    in.file = CreateFileW(source, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                          FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (in.file == INVALID_HANDLE_VALUE) return SZ_ERROR_READ;
    if (!GetFileSizeEx(in.file, &size) || offset > (uint64_t)size.QuadPart ||
        length > (uint64_t)size.QuadPart - offset) goto cleanup;
    seek.QuadPart = (LONGLONG)offset;
    if (!SetFilePointerEx(in.file, seek, NULL, FILE_BEGIN)) { result = SZ_ERROR_READ; goto cleanup; }
    out.file = CreateFileW(destination, GENERIC_WRITE, 0, NULL, CREATE_NEW,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (out.file == INVALID_HANDLE_VALUE) { result = SZ_ERROR_WRITE; goto cleanup; }
    LzmaEncProps_Init(&props);
    props.level = 5;
    props.dictSize = dictionary;
    props.lc = 3; props.lp = 0; props.pb = 2;
    props.algo = 1; props.btMode = 1; props.numHashBytes = 4;
    props.fb = fast_bytes;
    props.numThreads = threads;
    props.writeEndMark = 0;
    encoder = LzmaEnc_Create(&g_Alloc);
    if (!encoder) { result = SZ_ERROR_MEM; goto cleanup; }
    result = LzmaEnc_SetProps(encoder, &props);
    if (result != SZ_OK) goto cleanup;
    LzmaEnc_SetDataSize(encoder, length);
    result = LzmaEnc_WriteProperties(encoder, properties, &properties_size);
    if (result != SZ_OK) goto cleanup;
    if (write_output(&out.vt, properties, properties_size) != properties_size) {
        result = SZ_ERROR_WRITE; goto cleanup;
    }
    result = LzmaEnc_Encode(encoder, &out.vt, &in.vt, &progress.vt, &g_Alloc, &g_Alloc);
    if (result == SZ_OK && (in.remaining || out.failed)) result = SZ_ERROR_WRITE;
    if (result == SZ_OK && !FlushFileBuffers(out.file)) result = SZ_ERROR_WRITE;
cleanup:
    if (encoder) LzmaEnc_Destroy(encoder, &g_Alloc, &g_Alloc);
    if (out.file != INVALID_HANDLE_VALUE) {
        CloseHandle(out.file);
        if (result != SZ_OK) DeleteFileW(destination);
    }
    CloseHandle(in.file);
    return result;
}
