// history.cpp - 下载历史记录实现 (JSON, 追加式, 原子写)
#include "history.h"

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>

namespace pcl_dl {

static std::mutex g_hist_mtx;

std::wstring DefaultHistoryPath() {
    wchar_t buf[512] = {};
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, (DWORD)std::size(buf));
    if (n == 0 || n >= std::size(buf)) {
        n = GetEnvironmentVariableW(L"APPDATA", buf, (DWORD)std::size(buf));
    }
    std::wstring dir = (n > 0 && n < std::size(buf)) ? std::wstring(buf, n) : L".";
    return dir + L"\\AutoSlice\\history.json";
}

// JSON 字符串转义 (双引号/反斜杠/控制字符)
static std::string JsonEscape(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (unsigned char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char b[8];
                    snprintf(b, sizeof(b), "\\u%04x", c);
                    out += b;
                } else {
                    out += (char)c;
                }
        }
    }
    return out;
}

static bool ReadAllText(const std::wstring& path, std::string* out) {
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || !f) return false;
    std::string s;
    char buf[4096];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) s.append(buf, n);
    fclose(f);
    *out = std::move(s);
    return true;
}

// 原子写: 临时文件 + MoveFileEx 覆盖
static bool WriteAllText(const std::wstring& path, const std::string& s) {
    std::wstring tmp = path + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(s.data(), 1, s.size(), f);
    bool ok = (fclose(f) == 0 && w == s.size());
    if (ok) {
        if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            DeleteFileW(tmp.c_str());
            ok = false;
        }
    } else {
        DeleteFileW(tmp.c_str());
    }
    return ok;
}

static void RecordToJson(const DownloadRecord& r, std::string* out) {
    char buf[256];
    snprintf(buf, sizeof(buf),
             "{\"url\":\"%s\",\"filename\":\"%s\",\"path\":\"%s\",\"size\":%lld,\"success\":%s,"
             "\"error\":\"%s\",\"seconds\":%.1f,\"speed\":%lld,\"time\":%lld}",
             JsonEscape(r.url).c_str(), JsonEscape(r.filename).c_str(),
             JsonEscape(r.local_path).c_str(), (long long)r.size, r.success ? "true" : "false",
             JsonEscape(r.error).c_str(), r.seconds, (long long)r.speed_bps, (long long)r.timestamp);
    out->append(buf);
}

bool SaveHistory(const DownloadRecord& rec, const std::wstring& path) {
    std::wstring p = path.empty() ? DefaultHistoryPath() : path;
    std::lock_guard<std::mutex> lk(g_hist_mtx);

    // 确保目录存在
    std::wstring dir = p;
    auto pos = dir.find_last_of(L"\\/");
    if (pos != std::wstring::npos) {
        dir.resize(pos);
        CreateDirectoryW(dir.c_str(), nullptr);  // 已存在时静默失败, 可接受
    }

    std::string old;
    bool has_old = ReadAllText(p, &old);

    // 只接受我们自己的紧凑格式: [\n{...},\n{...}\n]; 其余视为损坏重建
    std::string entry;
    RecordToJson(rec, &entry);

    std::string body;
    int count = 0;
    if (has_old) {
        // 去掉首尾括号
        auto b = old.find('[');
        auto e = old.rfind(']');
        if (b != std::string::npos && e != std::string::npos && e > b) {
            body = old.substr(b + 1, e - b - 1);
            // 统计条目数: 每行以 "{" 开头 (紧凑格式保证)
            size_t i = 0;
            while ((i = body.find('\n', i)) != std::string::npos) {
                size_t j = i + 1;
                while (j < body.size() && (body[j] == ' ' || body[j] == '\t')) ++j;
                if (j < body.size() && body[j] == '{') ++count;
                i = j;
            }
        }
    }
    if (count >= kHistoryMaxRecords) {
        // 超限: 重建为仅保留最新 (截断最旧记录)。简单做法: 掐掉最前面的完整条目块。
        // 紧凑格式每条单行, 直接删除第一行含 "{" 的块 (保留后续)。
        size_t first = body.find('{');
        size_t end_lf = body.find('\n', first);
        if (first != std::string::npos && end_lf != std::string::npos) {
            body = body.substr(end_lf + 1);
        }
    }

    std::string out = "[\n";
    if (!body.empty()) {
        // 去掉尾部空白, 统一为 ",\n" 分隔的紧凑格式
        while (!body.empty() && (body.back() == '\n' || body.back() == ' ' || body.back() == '\t'))
            body.pop_back();
        out += body;
        out += ",\n";
    }
    out += entry;
    out += "\n]";

    return WriteAllText(p, out);
}

// ---- 读取: 极简解析器, 仅支持本模块生成的扁平对象数组 ----

static std::string JsonUnescape(const std::string& in, size_t* pos);

// 读取 JSON "..." 字符串 (已定位到引号), 返回解码后的字符串
static std::string ReadJsonString(const std::string& s, size_t* pos) {
    // 跳过已消费的起始引号
    std::string out;
    size_t i = *pos;
    while (i < s.size()) {
        char c = s[i++];
        if (c == '"') break;
        if (c == '\\' && i < s.size()) {
            char e = s[i++];
            switch (e) {
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'u': {
                    long code = strtol(s.c_str() + i, nullptr, 16);
                    i += 4;
                    if (code < 0x80) out += (char)code;
                    else if (code < 0x800) { out += (char)(0xC0 | (code >> 6)); out += (char)(0x80 | (code & 0x3F)); }
                    else { out += (char)(0xE0 | (code >> 12)); out += (char)(0x80 | ((code >> 6) & 0x3F)); out += (char)(0x80 | (code & 0x3F)); }
                    break;
                }
                default: out += e; break;
            }
        } else {
            out += c;
        }
    }
    *pos = i;
    return out;
}

static std::string FindStringField(const std::string& obj, const char* key) {
    std::string target = std::string("\"") + key + "\":";
    auto p = obj.find(target);
    if (p == std::string::npos) return {};
    p += target.size();
    while (p < obj.size() && (obj[p] == ' ' || obj[p] == '\t')) ++p;
    if (p >= obj.size() || obj[p] != '"') return {};
    size_t end = p + 1;
    return ReadJsonString(obj, &end);
}

static bool FindIntField(const std::string& obj, const char* key, std::int64_t* out) {
    std::string target = std::string("\"") + key + "\":";
    auto p = obj.find(target);
    if (p == std::string::npos) return false;
    p += target.size();
    while (p < obj.size() && (obj[p] == ' ' || obj[p] == '\t')) ++p;
    if (p >= obj.size()) return false;
    long long v = 0;
    if (obj[p] == '"') {  // 容忍引号包裹
        size_t end = p + 1;
        std::string s = ReadJsonString(obj, &end);
        v = atoll(s.c_str());
    } else {
        v = atoll(obj.c_str() + p);
    }
    *out = v;
    return true;
}

static bool FindBoolField(const std::string& obj, const char* key, bool* out) {
    std::string target = std::string("\"") + key + "\":";
    auto p = obj.find(target);
    if (p == std::string::npos) return false;
    p += target.size();
    while (p < obj.size() && (obj[p] == ' ' || obj[p] == '\t')) ++p;
    if (obj.compare(p, 4, "true") == 0 || obj.compare(p, 5, "false") == 0) {
        *out = obj.compare(p, 4, "true") == 0;
        return true;
    }
    if (p < obj.size() && obj[p] == '"') {  // 容忍引号包裹
        size_t end = p + 1;
        *out = ReadJsonString(obj, &end) == "true";
        return true;
    }
    return false;
}

static bool FindDoubleField(const std::string& obj, const char* key, double* out) {
    std::string target = std::string("\"") + key + "\":";
    auto p = obj.find(target);
    if (p == std::string::npos) return false;
    p += target.size();
    while (p < obj.size() && (obj[p] == ' ' || obj[p] == '\t')) ++p;
    if (p >= obj.size()) return false;
    *out = atof(obj.c_str() + p);
    return true;
}

std::vector<DownloadRecord> LoadHistory(const std::wstring& path) {
    std::wstring p = path.empty() ? DefaultHistoryPath() : path;
    std::vector<DownloadRecord> recs;
    std::lock_guard<std::mutex> lk(g_hist_mtx);

    std::string txt;
    if (!ReadAllText(p, &txt)) return recs;
    auto b = txt.find('[');
    auto e = txt.rfind(']');
    if (b == std::string::npos || e == std::string::npos || e <= b) return recs;

    size_t i = b;
    while (i < e) {
        size_t open = txt.find('{', i);
        if (open == std::string::npos || open >= e) break;
        size_t close = txt.find('}', open + 1);
        if (close == std::string::npos || close > e) break;
        std::string obj = txt.substr(open, close - open + 1);

        DownloadRecord r;
        r.url = FindStringField(obj, "url");
        r.filename = FindStringField(obj, "filename");
        r.local_path = FindStringField(obj, "path");
        r.error = FindStringField(obj, "error");
        std::int64_t tmp = 0;
        if (FindIntField(obj, "size", &tmp)) r.size = tmp;
        if (FindBoolField(obj, "success", &r.success)) {}
        if (FindDoubleField(obj, "seconds", &r.seconds)) {}
        if (FindIntField(obj, "speed", &tmp)) r.speed_bps = tmp;
        if (FindIntField(obj, "time", &tmp)) r.timestamp = tmp;
        recs.push_back(std::move(r));

        i = close + 1;
    }
    return recs;
}

}  // namespace pcl_dl