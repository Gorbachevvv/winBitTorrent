#pragma once

#include <string>
#include <functional>
#include <vector>

namespace winbittorrent
{
    class engine;

    engine* create_engine(std::string const& data_root);
    void destroy_engine(engine* instance) noexcept;
    std::string invoke_engine(engine& instance, std::string const& method, std::string const& payload);
    std::vector<char> create_torrent_data(std::string const& payload, std::function<bool(int, int)> const& progress);
}
