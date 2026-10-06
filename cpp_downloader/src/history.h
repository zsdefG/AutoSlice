// history.h - 下载历史记录 (JSON 文件, 追加式, 供 CLI/GUI/WPF 共享)
// 历史文件默认存放在 %LOCALAPPDATA%\AutoSlice\history.json
// 格式: JSON 数组, 每元素一条记录 (紧凑单行, 便于无损追加)
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace pcl_dl {

struct DownloadRecord {
    std::string url;          // 首个源 URL
    std::string filename;     // 文件名
    std::string local_path;   // 完整本地路径 (UTF-8)
    std::int64_t size = 0;    // 字节数 (成功时为实际大小)
    bool success = false;     // 是否下载成功
    std::string error;        // 失败原因 (成功时为空)
    double seconds = 0;       // 耗时 (秒)
    std::int64_t speed_bps = 0;  // 平均速度 (B/s)
    std::int64_t timestamp = 0;  // 完成时间 (Unix 秒)
};

// 默认历史文件路径 (%LOCALAPPDATA%\AutoSlice\history.json)
std::wstring DefaultHistoryPath();

// 追加一条记录 (线程安全, 原子写: 临时文件 + MoveFileEx 覆盖)。
// 文件损坏时自动重建; 超过 kHistoryMaxRecords 条时截断最旧记录。
bool SaveHistory(const DownloadRecord& rec, const std::wstring& path = L"");

// 读取全部记录 (时间正序, 最新在末尾)。文件不存在/损坏时返回空。
std::vector<DownloadRecord> LoadHistory(const std::wstring& path = L"");

// 历史记录条数上限
constexpr int kHistoryMaxRecords = 256;

}  // namespace pcl_dl