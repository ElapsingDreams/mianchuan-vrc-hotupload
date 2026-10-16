#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#include <string>
#include <vector>
#include <algorithm>
#include <ctype.h>
#include <string.h>

static const char* kCabPrefix = "CAB-";
static void SetWideBuffer(wchar_t* dest, int capacity, const std::wstring& value)
{
    if (!dest || capacity <= 0) return;
    int count = (int)value.size();
    if (count >= capacity) count = capacity - 1;
    if (count > 0)
        memcpy(dest, value.data(), count * sizeof(wchar_t));
    dest[count] = 0;
}

static std::wstring ToWide(const std::string& text)
{
    std::wstring out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size(); ++i)
        out.push_back((wchar_t)(unsigned char)text[i]);
    return out;
}

static std::string ToAscii(const wchar_t* text)
{
    if (!text) return std::string();
    std::string out;
    while (*text)
    {
        wchar_t c = *text++;
        out.push_back((c >= 0 && c <= 0x7f) ? (char)c : '?');
    }
    return out;
}

static bool ReadFileBytes(const wchar_t* path, std::vector<unsigned char>& bytes, std::wstring& error)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        error = L"failed to open file for reading";
        return false;
    }

    LARGE_INTEGER size;
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 0)
    {
        CloseHandle(h);
        error = L"failed to get file size";
        return false;
    }

    bytes.resize((size_t)size.QuadPart);
    size_t offset = 0;
    while (offset < bytes.size())
    {
        DWORD chunk = 0;
        DWORD want = (DWORD)std::min<size_t>(bytes.size() - offset, 16 * 1024 * 1024);
        if (!ReadFile(h, bytes.data() + offset, want, &chunk, NULL))
        {
            CloseHandle(h);
            error = L"failed to read file bytes";
            return false;
        }
        if (chunk == 0) break;
        offset += chunk;
    }

    CloseHandle(h);
    if (offset != bytes.size())
    {
        error = L"incomplete file read";
        return false;
    }
    return true;
}

static bool WriteFileBytes(const wchar_t* path, const std::vector<unsigned char>& bytes, std::wstring& error)
{
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        error = L"failed to open file for writing";
        return false;
    }

    size_t offset = 0;
    while (offset < bytes.size())
    {
        DWORD chunk = 0;
        DWORD want = (DWORD)std::min<size_t>(bytes.size() - offset, 16 * 1024 * 1024);
        if (!WriteFile(h, bytes.data() + offset, want, &chunk, NULL))
        {
            CloseHandle(h);
            error = L"failed to write file bytes";
            return false;
        }
        if (chunk == 0) break;
        offset += chunk;
    }

    CloseHandle(h);
    if (offset != bytes.size())
    {
        error = L"incomplete file write";
        return false;
    }
    return true;
}

static bool IsHexByte(unsigned char b)
{
    return (b >= (unsigned char)'0' && b <= (unsigned char)'9') ||
           (b >= (unsigned char)'a' && b <= (unsigned char)'f') ||
           (b >= (unsigned char)'A' && b <= (unsigned char)'F');
}

static bool IsAsciiEqual(const std::vector<unsigned char>& bytes, size_t offset, const char* text)
{
    size_t len = strlen(text);
    if (offset + len > bytes.size()) return false;
    for (size_t i = 0; i < len; ++i)
    {
        unsigned char b = bytes[offset + i];
        char c = text[i];
        if (b == (unsigned char)c) continue;
        if (c >= 'a' && c <= 'z' && b == (unsigned char)(c - 32)) continue;
        return false;
    }
    return true;
}

static bool LooksLikeAvatarId(const std::vector<unsigned char>& bytes, size_t offset)
{
    if (offset + 41 > bytes.size()) return false;
    for (size_t i = 5; i < 41; ++i)
    {
        size_t uuidIndex = i - 5;
        unsigned char b = bytes[offset + i];
        bool shouldHyphen = uuidIndex == 8 || uuidIndex == 13 || uuidIndex == 18 || uuidIndex == 23;
        if (shouldHyphen)
        {
            if (b != (unsigned char)'-') return false;
        }
        else if (!IsHexByte(b))
        {
            return false;
        }
    }
    return true;
}

static std::string LowerHex(const unsigned char* bytes, size_t length)
{
    static const char* hex = "0123456789abcdef";
    std::string out;
    out.reserve(length * 2);
    for (size_t i = 0; i < length; ++i)
    {
        unsigned char b = bytes[i];
        out.push_back(hex[(b >> 4) & 0x0f]);
        out.push_back(hex[b & 0x0f]);
    }
    return out;
}

static int ReplaceBytes(std::vector<unsigned char>& bytes, const std::string& oldText, const std::string& newText)
{
    if (oldText.size() != newText.size()) return -1;
    int count = 0;
    for (size_t i = 0; i + oldText.size() <= bytes.size(); ++i)
    {
        bool match = true;
        for (size_t j = 0; j < oldText.size(); ++j)
        {
            if (bytes[i + j] == (unsigned char)oldText[j]) continue;
            match = false;
            break;
        }
        if (!match) continue;

        memcpy(bytes.data() + i, newText.data(), newText.size());
        ++count;
        i += oldText.size() - 1;
    }
    return count;
}

static std::vector<std::string> FindAvatarIds(const std::vector<unsigned char>& bytes)
{
    std::vector<std::string> ids;
    for (size_t i = 0; i + 41 <= bytes.size(); ++i)
    {
        if (bytes[i] != (unsigned char)'a' && bytes[i] != (unsigned char)'A') continue;
        if (!IsAsciiEqual(bytes, i, "avtr_")) continue;
        if (!LooksLikeAvatarId(bytes, i)) continue;

        std::string id((const char*)bytes.data() + i, 41);
        bool exists = false;
        for (size_t j = 0; j < ids.size(); ++j)
        {
            if (_stricmp(ids[j].c_str(), id.c_str()) == 0)
            {
                exists = true;
                break;
            }
        }
        if (!exists) ids.push_back(id);
        i += 40;
    }
    return ids;
}

static std::vector<std::string> FindCabNames(const std::vector<unsigned char>& bytes)
{
    std::vector<std::string> names;
    for (size_t i = 0; i + 36 <= bytes.size(); ++i)
    {
        if (!IsAsciiEqual(bytes, i, kCabPrefix)) continue;
        bool ok = true;
        for (size_t j = 4; j < 36; ++j)
        {
            if (!IsHexByte(bytes[i + j]))
            {
                ok = false;
                break;
            }
        }
        if (!ok) continue;

        std::string name((const char*)bytes.data() + i, 36);
        bool exists = false;
        for (size_t j = 0; j < names.size(); ++j)
        {
            if (_stricmp(names[j].c_str(), name.c_str()) == 0)
            {
                exists = true;
                break;
            }
        }
        if (!exists) names.push_back(name);
        i += 35;
    }
    return names;
}

static bool LooksLikeCabName(const std::string& value)
{
    if (value.size() != 36) return false;
    if (_strnicmp(value.c_str(), kCabPrefix, 4) != 0) return false;
    for (size_t i = 4; i < value.size(); ++i)
    {
        char c = value[i];
        if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) continue;
        return false;
    }
    return true;
}

static bool ContainsAsciiInsensitive(const std::string& value, const std::string& needle)
{
    if (needle.empty() || needle.size() > value.size()) return false;
    for (size_t i = 0; i + needle.size() <= value.size(); ++i)
    {
        bool matched = true;
        for (size_t j = 0; j < needle.size(); ++j)
        {
            unsigned char left = (unsigned char)value[i + j];
            unsigned char right = (unsigned char)needle[j];
            if (tolower(left) == tolower(right)) continue;
            matched = false;
            break;
        }
        if (matched) return true;
    }
    return false;
}

static std::vector<std::string> FindAssetBundleNames(const std::vector<unsigned char>& bytes)
{
    std::vector<std::string> names;
    const char* prefix = "prefab-id-v1_";
    size_t prefixLen = strlen(prefix);
    for (size_t i = 0; i + prefixLen <= bytes.size(); ++i)
    {
        bool matched = true;
        for (size_t j = 0; j < prefixLen; ++j)
        {
            unsigned char b = bytes[i + j];
            char p = prefix[j];
            if (b == (unsigned char)p || b == (unsigned char)toupper((unsigned char)p)) continue;
            matched = false;
            break;
        }
        if (!matched) continue;

        size_t end = i;
        while (end < bytes.size() && bytes[end] >= 0x20 && bytes[end] <= 0x7e)
            ++end;
        std::string value((const char*)bytes.data() + i, end - i);
        if (value.size() >= 15 && _stricmp(value.c_str() + value.size() - 15, ".prefab.unity3d") == 0)
        {
            bool exists = false;
            for (size_t k = 0; k < names.size(); ++k)
            {
                if (_stricmp(names[k].c_str(), value.c_str()) == 0)
                {
                    exists = true;
                    break;
                }
            }
            if (!exists) names.push_back(value);
        }
        i = end > 0 ? end - 1 : i;
    }
    return names;
}

static unsigned int F(unsigned int x, unsigned int y, unsigned int z) { return (x & y) | (~x & z); }
static unsigned int G(unsigned int x, unsigned int y, unsigned int z) { return (x & y) | (x & z) | (y & z); }
static unsigned int H(unsigned int x, unsigned int y, unsigned int z) { return x ^ y ^ z; }
static unsigned int Rol(unsigned int x, int s) { return (x << s) | (x >> (32 - s)); }
static void FF(unsigned int& a, unsigned int b, unsigned int c, unsigned int d, unsigned int x, int s) { a = Rol(a + F(b, c, d) + x, s); }
static void GG(unsigned int& a, unsigned int b, unsigned int c, unsigned int d, unsigned int x, int s) { a = Rol(a + G(b, c, d) + x + 0x5a827999u, s); }
static void HH(unsigned int& a, unsigned int b, unsigned int c, unsigned int d, unsigned int x, int s) { a = Rol(a + H(b, c, d) + x + 0x6ed9eba1u, s); }

static void WriteUInt32LE(unsigned char* output, int offset, unsigned int value)
{
    output[offset] = (unsigned char)value;
    output[offset + 1] = (unsigned char)(value >> 8);
    output[offset + 2] = (unsigned char)(value >> 16);
    output[offset + 3] = (unsigned char)(value >> 24);
}

static std::string Md4Hex(const std::string& input)
{
    unsigned int a = 0x67452301u;
    unsigned int b = 0xefcdab89u;
    unsigned int c = 0x98badcfeu;
    unsigned int d = 0x10325476u;

    unsigned long long bitLength = (unsigned long long)input.size() * 8ull;
    size_t paddedLength = input.size() + 1;
    while ((paddedLength % 64) != 56) ++paddedLength;

    std::vector<unsigned char> data(paddedLength + 8);
    memcpy(data.data(), input.data(), input.size());
    data[input.size()] = 0x80;
    for (int i = 0; i < 8; ++i)
        data[paddedLength + i] = (unsigned char)(bitLength >> (8 * i));

    unsigned int x[16];
    for (size_t offset = 0; offset < data.size(); offset += 64)
    {
        for (int i = 0; i < 16; ++i)
        {
            x[i] = (unsigned int)(data[offset + i * 4] |
                                  (data[offset + i * 4 + 1] << 8) |
                                  (data[offset + i * 4 + 2] << 16) |
                                  (data[offset + i * 4 + 3] << 24));
        }

        unsigned int aa = a, bb = b, cc = c, dd = d;

        FF(a, b, c, d, x[0], 3); FF(d, a, b, c, x[1], 7); FF(c, d, a, b, x[2], 11); FF(b, c, d, a, x[3], 19);
        FF(a, b, c, d, x[4], 3); FF(d, a, b, c, x[5], 7); FF(c, d, a, b, x[6], 11); FF(b, c, d, a, x[7], 19);
        FF(a, b, c, d, x[8], 3); FF(d, a, b, c, x[9], 7); FF(c, d, a, b, x[10], 11); FF(b, c, d, a, x[11], 19);
        FF(a, b, c, d, x[12], 3); FF(d, a, b, c, x[13], 7); FF(c, d, a, b, x[14], 11); FF(b, c, d, a, x[15], 19);

        GG(a, b, c, d, x[0], 3); GG(d, a, b, c, x[4], 5); GG(c, d, a, b, x[8], 9); GG(b, c, d, a, x[12], 13);
        GG(a, b, c, d, x[1], 3); GG(d, a, b, c, x[5], 5); GG(c, d, a, b, x[9], 9); GG(b, c, d, a, x[13], 13);
        GG(a, b, c, d, x[2], 3); GG(d, a, b, c, x[6], 5); GG(c, d, a, b, x[10], 9); GG(b, c, d, a, x[14], 13);
        GG(a, b, c, d, x[3], 3); GG(d, a, b, c, x[7], 5); GG(c, d, a, b, x[11], 9); GG(b, c, d, a, x[15], 13);

        HH(a, b, c, d, x[0], 3); HH(d, a, b, c, x[8], 9); HH(c, d, a, b, x[4], 11); HH(b, c, d, a, x[12], 15);
        HH(a, b, c, d, x[2], 3); HH(d, a, b, c, x[10], 9); HH(c, d, a, b, x[6], 11); HH(b, c, d, a, x[14], 15);
        HH(a, b, c, d, x[1], 3); HH(d, a, b, c, x[9], 9); HH(c, d, a, b, x[5], 11); HH(b, c, d, a, x[13], 15);
        HH(a, b, c, d, x[3], 3); HH(d, a, b, c, x[11], 9); HH(c, d, a, b, x[7], 11); HH(b, c, d, a, x[15], 15);

        a += aa; b += bb; c += cc; d += dd;
    }

    unsigned char result[16];
    WriteUInt32LE(result, 0, a);
    WriteUInt32LE(result, 4, b);
    WriteUInt32LE(result, 8, c);
    WriteUInt32LE(result, 12, d);

    return LowerHex(result, 16);
}

static int ReplaceAllCabNames(std::vector<unsigned char>& bytes, const std::string& newCab, std::string& summary)
{
    summary.clear();
    std::vector<std::string> cabNames = FindCabNames(bytes);
    if (cabNames.empty())
    {
        summary = "(no CAB name found)";
        return 0;
    }

    int total = 0;
    for (size_t i = 0; i < cabNames.size(); ++i)
    {
        total += ReplaceBytes(bytes, cabNames[i], newCab);
        if (!summary.empty()) summary += "; ";
        summary += cabNames[i] + " -> " + newCab;
    }
    return total;
}

static unsigned int NextPseudoRandom()
{
    static unsigned int state = 0;
    if (state == 0)
    {
        state = (unsigned int)GetTickCount();
        state ^= (unsigned int)(uintptr_t)&state;
        if (state == 0) state = 0x6d2b79f5u;
    }
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
}

static std::string RandomCabName()
{
    static const char* hex = "0123456789abcdef";
    std::string out = kCabPrefix;
    out.reserve(36);
    for (int i = 0; i < 32; ++i)
        out.push_back(hex[NextPseudoRandom() & 0x0f]);
    return out;
}

static int RandomizeCabNames(std::vector<unsigned char>& bytes, std::string& summary)
{
    summary.clear();
    std::vector<std::string> cabNames = FindCabNames(bytes);
    if (cabNames.empty())
    {
        summary = "(no CAB name found)";
        return 0;
    }

    int total = 0;
    for (size_t i = 0; i < cabNames.size(); ++i)
    {
        std::string newCab = RandomCabName();
        total += ReplaceBytes(bytes, cabNames[i], newCab);
        if (!summary.empty()) summary += "; ";
        summary += cabNames[i] + " -> " + newCab;
    }
    return total;
}

static int MaybeRewriteCabNames(
    std::vector<unsigned char>& bytes,
    const std::string& cabOverrideInput,
    bool randomizeCab,
    const std::string& preferredAvatarId,
    std::string& summary,
    bool& invalidOverride,
    bool& ambiguousAssociation)
{
    invalidOverride = false;
    ambiguousAssociation = false;
    if (!cabOverrideInput.empty())
    {
        std::string cabOverride = cabOverrideInput;
        if (_strnicmp(cabOverride.c_str(), kCabPrefix, 4) != 0)
            cabOverride = std::string(kCabPrefix) + cabOverride;
        if (!LooksLikeCabName(cabOverride))
        {
            summary = "(invalid cab override)";
            invalidOverride = true;
            return 0;
        }
        return ReplaceAllCabNames(bytes, cabOverride, summary);
    }

    if (randomizeCab)
        return RandomizeCabNames(bytes, summary);

    std::vector<std::string> names = FindAssetBundleNames(bytes);
    std::vector<std::string> candidates;
    if (!preferredAvatarId.empty())
    {
        for (size_t i = 0; i < names.size(); ++i)
            if (ContainsAsciiInsensitive(names[i], preferredAvatarId))
                candidates.push_back(names[i]);
    }
    else if (names.size() == 1)
    {
        candidates.push_back(names[0]);
    }

    if (candidates.size() != 1)
    {
        summary = "asset bundle association is not unique: preferred=" + preferredAvatarId +
                  ", names=" + std::to_string(names.size()) +
                  ", matches=" + std::to_string(candidates.size());
        ambiguousAssociation = true;
        return 0;
    }

    std::vector<std::string> cabNames = FindCabNames(bytes);
    if (cabNames.size() != 1)
    {
        summary = "CAB association is not unique: names=" + std::to_string(cabNames.size());
        ambiguousAssociation = true;
        return 0;
    }

    std::string bundleName = candidates[0];
    std::string lower = bundleName;
    for (size_t i = 0; i < lower.size(); ++i)
    {
        if (lower[i] >= 'A' && lower[i] <= 'Z')
            lower[i] = (char)(lower[i] - 'A' + 'a');
    }

    std::string computedCab = std::string(kCabPrefix) + Md4Hex(lower);
    int replacements = ReplaceAllCabNames(bytes, computedCab, summary);
    summary = "MD4(assetBundleName='" + lower + "') => " + computedCab + "; " + summary;
    return replacements;

}

extern "C" __declspec(dllexport) int hu_rewrite_cab_names_file(
    const wchar_t* rawPath,
    const wchar_t* cabOverrideW,
    int randomizeCab,
    const wchar_t* preferredAvatarIdW,
    wchar_t* summaryOut,
    int summaryCapacity,
    int* cabReplaceCount)
{
    if (cabReplaceCount) *cabReplaceCount = 0;
    SetWideBuffer(summaryOut, summaryCapacity, L"");

    std::vector<unsigned char> bytes;
    std::wstring error;
    if (!ReadFileBytes(rawPath, bytes, error))
    {
        SetWideBuffer(summaryOut, summaryCapacity, error);
        return 5;
    }

    bool invalidOverride = false;
    bool ambiguousAssociation = false;
    std::string summary;
    int replacements = MaybeRewriteCabNames(
        bytes,
        ToAscii(cabOverrideW),
        randomizeCab != 0,
        ToAscii(preferredAvatarIdW),
        summary,
        invalidOverride,
        ambiguousAssociation);
    if (cabReplaceCount) *cabReplaceCount = replacements;
    SetWideBuffer(summaryOut, summaryCapacity, ToWide(summary));
    if (invalidOverride) return 7;
    if (ambiguousAssociation) return 9;

    if (replacements > 0 && !WriteFileBytes(rawPath, bytes, error))
    {
        SetWideBuffer(summaryOut, summaryCapacity, error);
        return 6;
    }
    return 0;
}
