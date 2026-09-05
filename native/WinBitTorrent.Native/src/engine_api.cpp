#include "engine_api.hpp"

#include <Windows.h>

#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/bdecode.hpp>
#include <libtorrent/hex.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session.hpp>
#include <libtorrent/session_stats.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/torrent_status.hpp>
#include <libtorrent/write_resume_data.hpp>
#include <libtorrent/create_torrent.hpp>
#include <libtorrent/entry.hpp>
#include <libtorrent/bencode.hpp>
#include <libtorrent/ip_filter.hpp>

#include <boost/json.hpp>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <limits>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string_view>
#include <system_error>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace lt = libtorrent;
namespace json = boost::json;
namespace fs = std::filesystem;

namespace
{
    std::string text(json::object const& object, std::string_view key, std::string fallback = {})
    {
        auto const* value = object.if_contains(key);
        return value != nullptr && value->is_string() ? std::string(value->as_string()) : std::move(fallback);
    }

    bool boolean(json::object const& object, std::string_view key, bool fallback = false)
    {
        auto const* value = object.if_contains(key);
        if (value == nullptr) return fallback;
        if (value->is_bool()) return value->as_bool();
        if (value->is_string()) return value->as_string() == "true" || value->as_string() == "1";
        return fallback;
    }

    std::int64_t integer(json::object const& object, std::string_view key, std::int64_t fallback = 0)
    {
        auto const* value = object.if_contains(key);
        if (value == nullptr) return fallback;
        if (value->is_int64()) return value->as_int64();
        if (value->is_uint64()) return static_cast<std::int64_t>(value->as_uint64());
        if (value->is_double()) return static_cast<std::int64_t>(value->as_double());
        if (value->is_string())
        {
            try { return std::stoll(std::string(value->as_string())); }
            catch (...) { return fallback; }
        }
        return fallback;
    }

    double number(json::object const& object, std::string_view key, double fallback = 0)
    {
        auto const* value = object.if_contains(key);
        if (value == nullptr) return fallback;
        if (value->is_double()) return value->as_double();
        if (value->is_int64()) return static_cast<double>(value->as_int64());
        if (value->is_uint64()) return static_cast<double>(value->as_uint64());
        if (value->is_string())
        {
            try { return std::stod(std::string(value->as_string())); }
            catch (...) { return fallback; }
        }
        return fallback;
    }

    std::vector<std::string> split(std::string const& value)
    {
        std::vector<std::string> result;
        std::string current;
        for (char character : value)
        {
            if (character == '|' || character == ',' || character == '\n')
            {
                if (!current.empty()) result.push_back(std::move(current));
                current.clear();
            }
            else if (character != '\r') current.push_back(character);
        }
        if (!current.empty()) result.push_back(std::move(current));
        for (auto& item : result)
        {
            auto const begin = item.find_first_not_of(" \t");
            auto const end = item.find_last_not_of(" \t");
            item = begin == std::string::npos ? std::string{} : item.substr(begin, end - begin + 1);
        }
        std::erase_if(result, [](std::string const& item) { return item.empty(); });
        return result;
    }

    std::vector<std::string> strings(json::object const& object, std::string_view key)
    {
        std::vector<std::string> result;
        auto const* value = object.if_contains(key);
        if (value == nullptr || !value->is_array()) return result;
        for (auto const& item : value->as_array())
            if (item.is_string()) result.emplace_back(item.as_string());
        return result;
    }

    std::string path_text(fs::path const& path)
    {
#if defined(_WIN32)
        auto const value = path.u8string();
        return { reinterpret_cast<char const*>(value.data()), value.size() };
#else
        return path.string();
#endif
    }

    fs::path utf8_path(std::string const& value)
    {
#if defined(_WIN32)
        auto const* begin = reinterpret_cast<char8_t const*>(value.data());
        return fs::path(std::u8string(begin, begin + value.size()));
#else
        return fs::path(value);
#endif
    }

    std::string hash_string(lt::sha1_hash const& hash)
    {
        return lt::aux::to_hex({ hash.data(), hash.size() });
    }

    std::string hash_string(lt::sha256_hash const& hash)
    {
        return lt::aux::to_hex({ hash.data(), hash.size() });
    }

    std::string primary_hash(lt::info_hash_t const& hashes)
    {
        if (hashes.has_v1()) return hash_string(hashes.v1);
        return hashes.has_v2() ? hash_string(hashes.v2) : std::string{};
    }

    std::string v1_hash(lt::info_hash_t const& hashes) { return hashes.has_v1() ? hash_string(hashes.v1) : std::string{}; }
    std::string v2_hash(lt::info_hash_t const& hashes) { return hashes.has_v2() ? hash_string(hashes.v2) : std::string{}; }

    std::string state_name(lt::torrent_status const& status)
    {
        bool const paused = bool(status.flags & lt::torrent_flags::paused);
        if (status.errc) return "error";
        switch (status.state)
        {
        case lt::torrent_status::checking_files: return status.is_finished ? "checkingUP" : "checkingDL";
        case lt::torrent_status::checking_resume_data: return "checkingResumeData";
        case lt::torrent_status::downloading_metadata: return paused ? "stoppedDL" : "metaDL";
        case lt::torrent_status::downloading: return paused ? "stoppedDL" : status.download_payload_rate == 0 ? "stalledDL" : "downloading";
        case lt::torrent_status::finished:
        case lt::torrent_status::seeding: return paused ? "stoppedUP" : status.upload_payload_rate == 0 ? "stalledUP" : "uploading";
        case lt::torrent_status::allocating: return "allocating";
        default: return paused ? "stoppedDL" : "unknown";
        }
    }

    std::vector<char> read_file(fs::path const& path)
    {
        std::ifstream input(path, std::ios::binary | std::ios::ate);
        if (!input) throw std::runtime_error("Unable to open " + path_text(path));
        auto const size = input.tellg();
        input.seekg(0);
        std::vector<char> result(static_cast<std::size_t>(size));
        input.read(result.data(), size);
        if (!input) throw std::runtime_error("Unable to read " + path_text(path));
        return result;
    }

    void write_atomic(fs::path const& path, std::vector<char> const& value)
    {
        auto temporary = path;
        temporary += ".tmp";
        {
            std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
            if (!output) throw std::runtime_error("Unable to write " + path_text(path));
            output.write(value.data(), static_cast<std::streamsize>(value.size()));
            output.flush();
            if (!output)
            {
                output.close();
                std::error_code ignored;
                fs::remove(temporary, ignored);
                throw std::runtime_error("Unable to flush " + path_text(path));
            }
        }
        if (!::MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            auto const error = static_cast<int>(::GetLastError());
            std::error_code ignored;
            fs::remove(temporary, ignored);
            throw std::system_error(error, std::system_category(), "Unable to replace " + path_text(path));
        }
    }

    json::object string_parameters(json::object const& payload)
    {
        auto const* parameters = payload.if_contains("parameters");
        if (parameters == nullptr || !parameters->is_object()) return {};
        json::object result;
        for (auto const& item : parameters->as_object())
        {
            if (item.value().is_string()) result[item.key()] = item.value().as_string();
            else if (item.value().is_null()) result[item.key()] = "";
            else result[item.key()] = json::serialize(item.value());
        }
        return result;
    }
}

namespace winbittorrent
{
    struct app_state
    {
        std::string category;
        std::set<std::string> tags;
        std::string display_name;
        std::string download_path;
        std::string complete_path;
        std::string pending_location;
        bool first_last = false;
        bool force_start = false;
        bool automatic_tmm = false;
        double ratio_limit = -1;
        int seeding_time_limit = -1;
        int inactive_seeding_time_limit = -1;
        int queue_position = -1;
        bool needs_recheck = false;
    };

    class engine
    {
    public:
        explicit engine(std::string const& data_root)
            : root_(utf8_path(data_root)), session_(default_settings())
        {
            torrent_root_ = root_ / "torrents";
            resume_root_ = root_ / "resume";
            fs::create_directories(torrent_root_);
            fs::create_directories(resume_root_);
            load_resume_files();
            session_.post_session_stats();
            last_stats_request_ = std::chrono::steady_clock::now();
            resume_saved_ = true;
        }

        ~engine()
        {
            // EngineState flushes and, in SQLite mode, captures the resulting files before
            // destroying the native session. Do not create a second uncaptured snapshot after
            // that flush; only save here when the caller did not already do so.
            try { if (!resume_saved_) save_resume_files(); }
            catch (...) {}
        }

        std::string invoke(std::string const& method, std::string const& payload_json)
        {
            std::scoped_lock lock(mutex_);
            drain_alerts();
            auto payload_value = json::parse(payload_json.empty() ? "{}" : payload_json);
            auto const& payload = payload_value.as_object();

            if (method == "sync.mainData") return json::serialize(main_data(payload));
            if (method == "sync.torrentPeers") return json::serialize(peers(payload));
            if (method == "transfer.info") return json::serialize(transfer_info());
            if (method == "transfer.getAlternativeLimits") return json::serialize(alternative_limits_);
            if (method == "transfer.setAlternativeLimits") { alternative_limits_ = boolean(payload, "enabled"); apply_global_limits(); return "{}"; }
            if (method == "transfer.getDownloadLimit") return json::serialize(download_limit_);
            if (method == "transfer.getUploadLimit") return json::serialize(upload_limit_);
            if (method == "transfer.setDownloadLimit") { download_limit_ = integer(payload, "value"); apply_global_limits(); return "{}"; }
            if (method == "transfer.setUploadLimit") { upload_limit_ = integer(payload, "value"); apply_global_limits(); return "{}"; }
            if (method == "transfer.banPeers") { ban_peers(payload); return "{}"; }
            if (method == "torrents.info") return json::serialize(torrents(payload));
            if (method == "torrents.properties") return json::serialize(properties(payload));
            if (method == "torrents.trackers") return json::serialize(trackers(payload));
            if (method == "torrents.webSeeds") return json::serialize(web_seeds(payload));
            if (method == "torrents.files") return json::serialize(files(payload));
            if (method == "torrents.pieceStates") return json::serialize(piece_states(payload));
            if (method == "torrents.pieceAvailability") return json::serialize(piece_availability(payload));
            if (method == "torrents.add") { add(payload); resume_saved_ = false; return "{}"; }
            if (method == "torrents.delete") { remove(payload); resume_saved_ = false; return "{}"; }
            if (method == "torrents.command") { command(payload); resume_saved_ = false; return "{}"; }
            if (method == "torrents.action") { action(payload); resume_saved_ = false; return "{}"; }
            if (method == "torrents.export") return json::serialize(export_bytes(payload));
            if (method == "torrents.parseMetadata") return json::serialize(parse_metadata(payload));
            if (method == "torrents.metadata") return json::serialize(metadata(payload));
            if (method == "engine.saveResume") { save_resume_files(); return "{}"; }
            if (method == "engine.poll")
            {
                auto errors = std::move(storage_errors_);
                storage_errors_ = {};
                auto const changed = storage_changed_;
                storage_changed_ = false;
                return json::serialize(json::object{ { "storage_errors", std::move(errors) }, { "storage_changed", changed } });
            }
            if (method == "engine.restoreAppState") { restore_app_state(payload); return "{}"; }
            if (method == "engine.applySettings") { apply_settings(payload); return "{}"; }
            throw std::runtime_error("Unsupported native method: " + method);
        }

    private:
        static lt::settings_pack default_settings()
        {
            lt::settings_pack settings;
            settings.set_bool(lt::settings_pack::enable_dht, true);
            settings.set_bool(lt::settings_pack::enable_lsd, true);
            settings.set_bool(lt::settings_pack::enable_upnp, true);
            settings.set_bool(lt::settings_pack::enable_natpmp, true);
            settings.set_bool(lt::settings_pack::enable_incoming_utp, true);
            settings.set_bool(lt::settings_pack::enable_outgoing_utp, true);
            settings.set_int(lt::settings_pack::alert_mask,
                lt::alert_category::error | lt::alert_category::status | lt::alert_category::storage);
            settings.set_str(lt::settings_pack::user_agent, "WinBitTorrent/1.0");
            return settings;
        }

        static bool resume_files_changed_after_snapshot(
            lt::bdecode_node const& root,
            lt::add_torrent_params const& params)
        {
            if (!params.ti || bool(params.flags & lt::torrent_flags::seed_mode)) return false;

            auto const pieces = root.dict_find_string_value("pieces");
            if (!pieces.empty()
                && std::none_of(pieces.begin(), pieces.end(), [](char value) { return value == 0; }))
                return false;

            auto const snapshot_time = std::max(
                root.dict_find_int_value("last_download", 0),
                root.dict_find_int_value("completed_time", 0));
            auto const save_path = utf8_path(params.save_path);
            auto const& files = params.ti->files();
            for (lt::file_index_t index{ 0 }; index < files.num_files(); ++index)
            {
                if (files.pad_file_at(index)) continue;
                auto const path = save_path / utf8_path(files.file_path(index));
                std::error_code error;
                auto const size = fs::file_size(path, error);
                if (error || size != static_cast<std::uintmax_t>(files.file_size(index))) continue;
                auto const modified = fs::last_write_time(path, error);
                if (error) continue;
                auto const modified_system = std::chrono::time_point_cast<std::chrono::system_clock::duration>(
                    modified - fs::file_time_type::clock::now() + std::chrono::system_clock::now());
                auto const modified_seconds = std::chrono::system_clock::to_time_t(modified_system);
                if (snapshot_time == 0 || modified_seconds > snapshot_time + 1)
                    return true;
            }
            return false;
        }

        void load_resume_files()
        {
            for (auto const& item : fs::directory_iterator(resume_root_))
            {
                if (!item.is_regular_file() || item.path().extension() != ".fastresume") continue;
                try
                {
                    auto buffer = read_file(item.path());
                    lt::error_code error;
                    auto const root = lt::bdecode(
                        { buffer.data(), static_cast<std::ptrdiff_t>(buffer.size()) }, error);
                    if (error || root.type() != lt::bdecode_node::dict_t) continue;
                    app_state imported;
                    imported.category = std::string(root.dict_find_string_value("qBt-category"));
                    imported.display_name = std::string(root.dict_find_string_value("qBt-name"));
                    imported.download_path = std::string(root.dict_find_string_value("qBt-downloadPath"));
                    imported.automatic_tmm = root.dict_find_int_value("qBt-autoTMM") != 0;
                    imported.first_last = root.dict_find_int_value("qBt-firstLastPiecePriority") != 0;
                    imported.seeding_time_limit = static_cast<int>(root.dict_find_int_value("qBt-seedingTimeLimit", -1));
                    imported.inactive_seeding_time_limit = static_cast<int>(root.dict_find_int_value("qBt-inactiveSeedingTimeLimit", -1));
                    auto const ratio = root.dict_find("qBt-ratioLimit");
                    if (ratio.type() == lt::bdecode_node::string_t)
                    {
                        try { imported.ratio_limit = std::stod(std::string(ratio.string_value())); }
                        catch (...) { imported.ratio_limit = -1; }
                    }
                    else imported.ratio_limit = root.dict_find_int_value("qBt-ratioLimit", -1000) / 1000.0;
                    auto const tag_values = root.dict_find("qBt-tags");
                    if (tag_values.type() == lt::bdecode_node::list_t)
                        for (int index = 0; index < tag_values.list_size(); ++index)
                            imported.tags.insert(std::string(tag_values.list_string_value_at(index)));
                    auto params = lt::read_resume_data(
                        { buffer.data(), static_cast<std::ptrdiff_t>(buffer.size()) }, error);
                    if (error) continue;
                    auto torrent_path = torrent_root_ / (item.path().stem().string() + ".torrent");
                    if (!params.ti && fs::exists(torrent_path))
                        params.ti = std::make_shared<lt::torrent_info>(path_text(torrent_path), error);
                    if (!error)
                    {
                        auto const needs_recheck = resume_files_changed_after_snapshot(root, params);
                        auto const pause_after_recheck = needs_recheck
                            && bool(params.flags & lt::torrent_flags::paused);
                        if (needs_recheck)
                        {
                            params.have_pieces.clear();
                            params.verified_pieces.clear();
                            params.unfinished_pieces.clear();
                            params.flags &= ~lt::torrent_flags::paused;
                            params.flags &= ~lt::torrent_flags::auto_managed;
                            if (pause_after_recheck)
                                params.flags |= lt::torrent_flags::upload_mode;
                        }
                        auto handle = session_.add_torrent(std::move(params), error);
                        if (!error)
                        {
                            states_[primary_hash(handle.info_hashes())] = std::move(imported);
                            set_first_last(handle, state(handle).first_last);
                            global_tags_.insert(state(handle).tags.begin(), state(handle).tags.end());
                            if (pause_after_recheck)
                                pause_after_recheck_.insert(primary_hash(handle.info_hashes()));
                        }
                    }
                }
                catch (...) {}
            }
            // A damaged or missing resume file must never make an existing torrent disappear.
            // Import its metadata in a stopped state and let the UI report that a hash check is
            // required before the user starts it.
            for (auto const& item : fs::directory_iterator(torrent_root_))
            {
                if (!item.is_regular_file() || item.path().extension() != ".torrent") continue;
                auto const expected_hash = item.path().stem().string();
                bool already_loaded = false;
                for (auto const& handle : session_.get_torrents())
                {
                    auto const hashes = handle.info_hashes();
                    if (primary_hash(hashes) == expected_hash || v1_hash(hashes) == expected_hash || v2_hash(hashes) == expected_hash)
                    {
                        already_loaded = true;
                        break;
                    }
                }
                if (already_loaded) continue;
                try
                {
                    lt::error_code error;
                    lt::add_torrent_params params;
                    params.ti = std::make_shared<lt::torrent_info>(path_text(item.path()), error);
                    if (error) continue;
                    params.save_path = path_text(root_);
                    params.flags |= lt::torrent_flags::paused;
                    params.flags &= ~lt::torrent_flags::auto_managed;
                    session_.add_torrent(std::move(params), error);
                }
                catch (...) {}
            }
        }

        void drain_alerts()
        {
            std::vector<lt::alert*> alerts;
            session_.pop_alerts(&alerts);
            std::vector<lt::torrent_handle> completed;
            for (auto const* alert : alerts)
                process_alert(alert, completed);
            if (!completed.empty())
                save_resume_files(std::move(completed), false);
            if (std::chrono::steady_clock::now() - last_stats_request_ >= std::chrono::seconds(1))
            {
                session_.post_session_stats();
                last_stats_request_ = std::chrono::steady_clock::now();
            }
            enforce_share_limits();
        }

        void process_alert(lt::alert const* alert, std::vector<lt::torrent_handle>& completed)
        {
            persist_resume_alert(alert);
            if (auto const* statistics = lt::alert_cast<lt::session_stats_alert>(alert))
            {
                auto const counters = statistics->counters();
                if (dht_nodes_metric_ >= 0)
                {
                    auto const metric = static_cast<decltype(counters.size())>(dht_nodes_metric_);
                    if (metric < counters.size()) dht_nodes_ = static_cast<int>(counters[metric]);
                }
            }
            if (auto const* finished = lt::alert_cast<lt::torrent_finished_alert>(alert))
            {
                auto const& extra = state(finished->handle);
                if (extra.pending_location.empty() && !extra.complete_path.empty() && finished->handle.status().save_path != extra.complete_path)
                    finished->handle.move_storage(extra.complete_path);
                auto const hash = primary_hash(finished->handle.info_hashes());
                if (recheck_completed_ && rechecked_completed_.insert(hash).second)
                    finished->handle.force_recheck();
                completed.push_back(finished->handle);
                resume_saved_ = false;
            }
            if (auto const* checked = lt::alert_cast<lt::torrent_checked_alert>(alert))
            {
                auto const hash = primary_hash(checked->handle.info_hashes());
                if (pause_after_recheck_.erase(hash) > 0)
                {
                    checked->handle.pause();
                    checked->handle.unset_flags(lt::torrent_flags::upload_mode);
                }
                completed.push_back(checked->handle);
                resume_saved_ = false;
            }
            if (auto const* metadata = lt::alert_cast<lt::metadata_received_alert>(alert))
            {
                persist_torrent_file(metadata->handle);
                completed.push_back(metadata->handle);
                resume_saved_ = false;
            }
            if (auto const* moved = lt::alert_cast<lt::storage_moved_alert>(alert))
            {
                auto& extra = state(moved->handle);
                if (!extra.pending_location.empty())
                {
                    extra.complete_path = moved->storage_path();
                    extra.download_path.clear();
                    extra.pending_location.clear();
                }
                completed.push_back(moved->handle);
                storage_changed_ = true;
                resume_saved_ = false;
            }
            if (auto const* failed = lt::alert_cast<lt::storage_moved_failed_alert>(alert))
            {
                state(failed->handle).pending_location.clear();
                // Keep the last confirmed path; a failed move must not be persisted as success.
                if (storage_errors_.size() < 100)
                    storage_errors_.emplace_back(failed->message());
                completed.push_back(failed->handle);
                storage_changed_ = true;
                resume_saved_ = false;
            }
        }

        void enforce_share_limits()
        {
            auto const now = lt::clock_type::now();
            std::vector<lt::torrent_handle> stopped;
            for (auto const& handle : session_.get_torrents())
            {
                if (!handle.is_valid()) continue;
                auto const status = handle.status();
                if (!status.is_finished) continue;
                auto const& limits = state(handle);
                bool reached = limits.ratio_limit >= 0 && status.all_time_download > 0
                    && double(status.all_time_upload) / double(status.all_time_download) >= limits.ratio_limit;
                reached = reached || limits.seeding_time_limit >= 0
                    && status.seeding_duration >= std::chrono::minutes(limits.seeding_time_limit);
                reached = reached || limits.inactive_seeding_time_limit >= 0
                    && status.last_upload != lt::time_point{}
                    && now - status.last_upload >= std::chrono::minutes(limits.inactive_seeding_time_limit);
                if (reached && !(status.flags & lt::torrent_flags::paused))
                {
                    handle.unset_flags(lt::torrent_flags::auto_managed);
                    handle.pause();
                    stopped.push_back(handle);
                    resume_saved_ = false;
                }
            }
            if (!stopped.empty()) save_resume_files(std::move(stopped), false);
        }

        static std::string peer_address(std::string value)
        {
            auto const begin = value.find_first_not_of(" \t");
            auto const end = value.find_last_not_of(" \t");
            value = begin == std::string::npos ? std::string{} : value.substr(begin, end - begin + 1);
            if (value.starts_with('['))
            {
                auto const close = value.find(']');
                if (close != std::string::npos) return value.substr(1, close - 1);
            }
            auto const colon = value.rfind(':');
            if (colon != std::string::npos && value.find(':') == colon
                && std::all_of(value.begin() + static_cast<std::ptrdiff_t>(colon + 1), value.end(), [](char c) { return c >= '0' && c <= '9'; }))
                return value.substr(0, colon);
            return value;
        }

        void ban_peers(json::object const& payload)
        {
            auto filter = session_.get_ip_filter();
            auto const* values = payload.if_contains("peers");
            if (values == nullptr || !values->is_array()) return;
            for (auto const& value : values->as_array())
            {
                if (!value.is_string()) continue;
                lt::error_code error;
                auto const address = lt::make_address(peer_address(std::string(value.as_string())), error);
                if (!error) filter.add_rule(address, address, lt::ip_filter::blocked);
            }
            session_.set_ip_filter(std::move(filter));
        }

        void persist_resume_alert(lt::alert const* alert)
        {
            auto const* saved = lt::alert_cast<lt::save_resume_data_alert>(alert);
            if (saved == nullptr) return;
            auto const hash = primary_hash(saved->params.info_hashes);
            if (hash.empty()) return;
            write_atomic(resume_root_ / (hash + ".fastresume"), lt::write_resume_data_buf(saved->params));
            if (saved->params.ti)
            {
                try
                {
                    write_atomic(torrent_root_ / (hash + ".torrent"),
                        lt::write_torrent_file_buf(saved->params, lt::write_flags::allow_missing_piece_layer));
                }
                catch (std::exception const&) {}
            }
        }

        void save_resume_files()
        {
            save_resume_files(session_.get_torrents(), true);
        }

        void save_resume_files(std::vector<lt::torrent_handle> handles, bool complete_snapshot)
        {
            std::unordered_set<std::string> pending;
            bool save_failed = false;
            auto request_save = [&](lt::torrent_handle const& handle)
            {
                if (!handle.is_valid()) return;
                auto const hash = primary_hash(handle.info_hashes());
                if (hash.empty() || !pending.insert(hash).second) return;
                persist_torrent_file(handle);
                handle.save_resume_data();
            };
            for (auto const& handle : handles) request_save(handle);

            auto const deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
            while (!pending.empty() && std::chrono::steady_clock::now() < deadline)
            {
                if (session_.wait_for_alert(std::chrono::milliseconds(200)) == nullptr) continue;
                std::vector<lt::alert*> alerts;
                session_.pop_alerts(&alerts);
                std::vector<lt::torrent_handle> completed;
                for (auto const* alert : alerts)
                {
                    if (auto const* saved = lt::alert_cast<lt::save_resume_data_alert>(alert))
                    {
                        persist_resume_alert(alert);
                        pending.erase(primary_hash(saved->params.info_hashes));
                    }
                    else if (auto const* failure = lt::alert_cast<lt::save_resume_data_failed_alert>(alert))
                    {
                        pending.erase(primary_hash(failure->handle.info_hashes()));
                        save_failed = true;
                    }
                    else
                    {
                        process_alert(alert, completed);
                    }
                }
                for (auto const& handle : completed) request_save(handle);
            }
            if (complete_snapshot)
                resume_saved_ = pending.empty() && !save_failed;
            else if (!pending.empty() || save_failed)
                resume_saved_ = false;
        }

        lt::torrent_handle require(std::string const& hash) const
        {
            for (auto const& handle : session_.get_torrents())
            {
                auto const hashes = handle.info_hashes();
                if (primary_hash(hashes) == hash || v1_hash(hashes) == hash || v2_hash(hashes) == hash) return handle;
            }
            throw std::runtime_error("Torrent not found: " + hash);
        }

        std::vector<lt::torrent_handle> selected(std::string const& hashes) const
        {
            if (hashes == "all") return session_.get_torrents();
            std::vector<lt::torrent_handle> result;
            for (auto const& hash : split(hashes)) result.push_back(require(hash));
            return result;
        }

        app_state& state(lt::torrent_handle const& handle) { return states_[primary_hash(handle.info_hashes())]; }

        app_state const& state(lt::torrent_handle const& handle) const
        {
            static app_state const empty;
            auto const item = states_.find(primary_hash(handle.info_hashes()));
            return item == states_.end() ? empty : item->second;
        }

        static std::string joined_tags(std::set<std::string> const& tags)
        {
            std::string result;
            for (auto const& tag : tags)
            {
                if (!result.empty()) result += ", ";
                result += tag;
            }
            return result;
        }

        static std::string content_path(lt::torrent_status const& status)
        {
            auto const info = status.torrent_file.lock();
            if (!info) return status.save_path;
            auto const root = utf8_path(status.save_path);
            if (info->num_files() == 1)
                return path_text(root / utf8_path(info->files().file_path(lt::file_index_t{ 0 })));
            return path_text(root / utf8_path(info->name()));
        }

        json::object torrent(lt::torrent_handle const& handle) const
        {
            auto const status = handle.status();
            auto const info = status.torrent_file.lock();
            auto const& extra = state(handle);
            auto const total = status.total_wanted > 0 ? status.total_wanted : status.total;
            auto const left = std::max<std::int64_t>(0, total - status.total_wanted_done);
            auto const ratio = status.all_time_download > 0 ? double(status.all_time_upload) / status.all_time_download : 0.0;
            auto const tracker_entries = handle.trackers();
            auto const name = extra.display_name.empty() ? status.name : extra.display_name;
            return {
                { "hash", primary_hash(status.info_hashes) }, { "name", name },
                { "size", total }, { "total_size", status.total }, { "progress", status.progress },
                { "state", state_name(status) }, { "num_seeds", status.num_seeds },
                { "num_complete", std::max(status.num_complete, 0) },
                { "num_leechs", std::max(status.num_peers - status.num_seeds, 0) },
                { "num_incomplete", std::max(status.num_incomplete, 0) },
                { "dlspeed", status.download_payload_rate }, { "upspeed", status.upload_payload_rate },
                { "eta", status.download_payload_rate > 0 ? left / status.download_payload_rate : 8640000 },
                { "ratio", ratio }, { "popularity", 0.0 }, { "category", extra.category },
                { "tags", joined_tags(extra.tags) }, { "added_on", status.added_time },
                { "completion_on", status.completed_time }, { "created_on", info ? info->creation_date() : 0 },
                { "tracker", tracker_entries.empty() ? "" : tracker_entries.front().url },
                { "dl_limit", handle.download_limit() }, { "up_limit", handle.upload_limit() },
                { "downloaded", status.all_time_download }, { "uploaded", status.all_time_upload },
                { "downloaded_session", status.total_download }, { "uploaded_session", status.total_upload },
                { "amount_left", left }, { "time_active", status.active_duration.count() },
                { "save_path", extra.complete_path.empty() ? status.save_path : extra.complete_path }, { "content_path", content_path(status) },
                { "completed", status.total_wanted_done }, { "ratio_limit", extra.ratio_limit },
                { "seen_complete", status.last_seen_complete }, { "last_activity", 0 },
                { "availability", status.distributed_copies }, { "download_path", extra.download_path },
                { "infohash_v1", v1_hash(status.info_hashes) }, { "infohash_v2", v2_hash(status.info_hashes) },
                { "reannounce", status.next_announce.count() }, { "private", info && info->priv() },
                { "priority", static_cast<int>(status.queue_position) }, { "force_start", extra.force_start },
                { "seq_dl", bool(status.flags & lt::torrent_flags::sequential_download) },
                { "f_l_piece_prio", extra.first_last },
                { "super_seeding", bool(status.flags & lt::torrent_flags::super_seeding) },
                { "display_name", extra.display_name },
                { "auto_tmm", extra.automatic_tmm },
                { "seeding_time_limit", extra.seeding_time_limit },
                { "inactive_seeding_time_limit", extra.inactive_seeding_time_limit }
            };
        }

        json::array torrents(json::object const& payload) const
        {
            auto const filter = text(payload, "filter", "all");
            auto const category = text(payload, "category");
            auto const tag = text(payload, "tag");
            json::array result;
            for (auto const& handle : session_.get_torrents())
            {
                auto const status = handle.status();
                auto const name = state_name(status);
                auto const& extra = state(handle);
                bool const include = filter == "all"
                    || (filter == "downloading" && (name == "downloading" || name == "stalledDL" || name == "metaDL"))
                    || (filter == "seeding" && (name == "uploading" || name == "stalledUP"))
                    || (filter == "completed" && status.is_finished)
                    || (filter == "paused" && bool(status.flags & lt::torrent_flags::paused))
                    || (filter == "active" && (status.download_payload_rate > 0 || status.upload_payload_rate > 0))
                    || (filter == "inactive" && status.download_payload_rate == 0 && status.upload_payload_rate == 0)
                    || (filter == "stalled" && name.starts_with("stalled"))
                    || (filter == "errored" && bool(status.errc));
                if (include && (category.empty() || category == extra.category) && (tag.empty() || extra.tags.contains(tag)))
                    result.push_back(torrent(handle));
            }
            return result;
        }

        json::object server_state() const
        {
            std::int64_t down = 0, up = 0, all_down = 0, all_up = 0;
            for (auto const& handle : session_.get_torrents())
            {
                auto const status = handle.status();
                down += status.download_payload_rate;
                up += status.upload_payload_rate;
                all_down += status.all_time_download;
                all_up += status.all_time_upload;
            }
            std::error_code error;
            auto const disk = fs::space(root_, error);
            return {
                { "connection_status", "connected" }, { "dht_nodes", dht_nodes_ },
                { "dl_info_speed", down }, { "up_info_speed", up },
                { "dl_info_data", all_down }, { "up_info_data", all_up },
                { "alltime_dl", all_down }, { "alltime_ul", all_up },
                { "free_space_on_disk", error ? 0 : static_cast<std::int64_t>(disk.available) },
                { "use_alt_speed_limits", alternative_limits_ }, { "queueing", queueing_enabled_ }, { "refresh_interval", 1000 }
            };
        }

        json::object main_data(json::object const& payload) const
        {
            json::object torrent_map;
            for (auto const& handle : session_.get_torrents())
                torrent_map[primary_hash(handle.info_hashes())] = torrent(handle);
            json::object category_map;
            for (auto const& [name, paths] : categories_)
                category_map[name] = { { "name", name }, { "savePath", paths.first }, { "downloadPath", paths.second } };
            json::array tags;
            for (auto const& tag : global_tags_) tags.push_back(json::value(tag));
            return {
                { "rid", integer(payload, "responseId") + 1 }, { "full_update", true },
                { "torrents", std::move(torrent_map) }, { "categories", std::move(category_map) },
                { "tags", std::move(tags) }, { "server_state", server_state() }
            };
        }

        json::object transfer_info() const
        {
            auto values = server_state();
            return {
                { "dl_info_speed", values.at("dl_info_speed") }, { "up_info_speed", values.at("up_info_speed") },
                { "dl_info_data", values.at("dl_info_data") }, { "up_info_data", values.at("up_info_data") },
                { "connection_status", "connected" }
            };
        }

        json::object properties(json::object const& payload) const
        {
            auto const handle = require(text(payload, "hash"));
            auto const status = handle.status();
            auto const info = status.torrent_file.lock();
            auto const& extra = state(handle);
            auto const left = std::max<std::int64_t>(0, status.total_wanted - status.total_wanted_done);
            return {
                { "time_elapsed", status.active_duration.count() },
                { "eta", status.download_payload_rate > 0 ? left / status.download_payload_rate : 8640000 },
                { "nb_connections", status.num_connections }, { "total_downloaded", status.all_time_download },
                { "total_uploaded", status.all_time_upload },
                { "total_wasted", status.total_redundant_bytes + status.total_failed_bytes },
                { "seeds", status.num_seeds }, { "peers", std::max(status.num_peers - status.num_seeds, 0) },
                { "share_ratio", status.all_time_download > 0 ? double(status.all_time_upload) / status.all_time_download : 0 },
                { "piece_size", info ? info->piece_length() : 0 }, { "pieces_num", info ? info->num_pieces() : 0 },
                { "comment", info ? info->comment() : "" }, { "created_by", info ? info->creator() : "" },
                { "addition_date", status.added_time }, { "completion_date", status.completed_time },
                { "save_path", status.save_path }, { "seeding_time", status.seeding_duration.count() },
                { "seeding_time_limit", extra.seeding_time_limit },
                { "inactive_seeding_time_limit", extra.inactive_seeding_time_limit }
            };
        }

        json::array trackers(json::object const& payload) const
        {
            json::array result;
            result.push_back({ { "url", "** [DHT] **" }, { "status", 0 }, { "tier", 0 }, { "num_seeds", -1 }, { "num_leeches", -1 }, { "msg", "" } });
            result.push_back({ { "url", "** [PeX] **" }, { "status", 0 }, { "tier", 0 }, { "num_seeds", -1 }, { "num_leeches", -1 }, { "msg", "" } });
            result.push_back({ { "url", "** [LSD] **" }, { "status", 0 }, { "tier", 0 }, { "num_seeds", -1 }, { "num_leeches", -1 }, { "msg", "" } });
            for (auto const& entry : require(text(payload, "hash")).trackers())
                result.push_back({ { "url", entry.url }, { "status", 0 }, { "tier", entry.tier }, { "num_seeds", -1 }, { "num_leeches", -1 }, { "msg", "" } });
            return result;
        }

        json::array web_seeds(json::object const& payload) const
        {
            auto const handle = require(text(payload, "hash"));
            json::array result;
            for (auto const& value : handle.url_seeds()) result.push_back(json::value(value));
            for (auto const& value : handle.http_seeds()) result.push_back(json::value(value));
            return result;
        }

        static int api_priority(lt::download_priority_t value)
        {
            auto const priority = static_cast<int>(value);
            if (priority <= 0) return 0;
            if (priority <= 1) return 1;
            if (priority <= 4) return 6;
            return 7;
        }

        static lt::download_priority_t native_priority(int value)
        {
            if (value <= 0) return lt::dont_download;
            if (value >= 7) return lt::top_priority;
            if (value >= 6) return lt::download_priority_t{ 6 };
            return lt::default_priority;
        }

        json::array files(json::object const& payload) const
        {
            auto const handle = require(text(payload, "hash"));
            auto const status = handle.status();
            auto const info = status.torrent_file.lock();
            if (!info) return {};
            std::vector<std::int64_t> progress;
            handle.file_progress(progress);
            auto const priorities = handle.get_file_priorities();
            json::array result;
            for (lt::file_index_t index{ 0 }; index < info->num_files(); ++index)
            {
                auto const offset = static_cast<std::size_t>(static_cast<int>(index));
                auto const size = info->files().file_size(index);
                auto const done = offset < progress.size() ? progress[offset] : 0;
                auto const priority = offset < priorities.size() ? priorities[offset] : lt::default_priority;
                result.push_back({ { "index", static_cast<int>(index) }, { "name", info->files().file_path(index) },
                    { "size", size }, { "progress", size > 0 ? double(done) / size : 1 },
                    { "priority", api_priority(priority) }, { "is_seed", status.is_seeding },
                    { "availability", status.distributed_copies } });
            }
            return result;
        }

        json::array piece_states(json::object const& payload) const
        {
            auto const handle = require(text(payload, "hash"));
            auto const status = handle.status(lt::torrent_handle::query_pieces);
            std::vector<int> states;
            states.reserve(status.pieces.size());
            for (bool value : status.pieces) states.push_back(value ? 2 : 0);

            // status.pieces only distinguishes complete/missing. Mark pieces currently being
            // requested or written so the managed UI can render its documented yellow state.
            for (auto const& piece : handle.get_download_queue())
            {
                auto const index = static_cast<std::size_t>(static_cast<int>(piece.piece_index));
                if (index < states.size() && states[index] == 0)
                    states[index] = 1;
            }

            json::array result;
            for (int value : states) result.push_back(value);
            return result;
        }

        json::array piece_availability(json::object const& payload) const
        {
            auto const handle = require(text(payload, "hash"));
            std::vector<int> availability;
            handle.piece_availability(availability);

            // A piece present locally is available even when there are no connected peers. This
            // also compensates for libtorrent not maintaining picker availability while seeding.
            auto const status = handle.status(lt::torrent_handle::query_pieces);
            auto const count = std::min(static_cast<int>(availability.size()), status.pieces.size());
            for (int index = 0; index < count; ++index)
                if (status.pieces[lt::piece_index_t{ index }])
                    ++availability[static_cast<std::size_t>(index)];

            json::array result;
            for (int value : availability) result.push_back(value);
            return result;
        }

        json::object peers(json::object const& payload) const
        {
            std::vector<lt::peer_info> values;
            require(text(payload, "hash")).get_peer_info(values);
            json::object peers_object;
            for (auto const& peer : values)
            {
                auto const address = peer.ip.address().to_string();
                auto const id = address + ":" + std::to_string(peer.ip.port());
                std::string flags;
                if (peer.flags & lt::peer_info::interesting) flags += "d";
                if (!(peer.flags & lt::peer_info::choked)) flags += "U";
                if (peer.flags & lt::peer_info::remote_interested) flags += "u";
                if (!(peer.flags & lt::peer_info::remote_choked)) flags += "D";
                peers_object[id] = { { "ip", address }, { "port", peer.ip.port() }, { "country", "" },
                    { "country_code", "" }, { "client", peer.client },
                    { "connection", bool(peer.flags & lt::peer_info::utp_socket) ? "uTP" : "TCP" },
                    { "flags", flags }, { "progress", peer.progress }, { "dl_speed", peer.payload_down_speed },
                    { "up_speed", peer.payload_up_speed }, { "downloaded", peer.total_download }, { "uploaded", peer.total_upload } };
            }
            return { { "rid", integer(payload, "responseId") + 1 }, { "full_update", true }, { "peers", std::move(peers_object) } };
        }

        void configure(lt::add_torrent_params& params, json::object const& payload)
        {
            auto const complete_path = text(payload, "savePath", default_save_path_.empty() ? path_text(root_) : default_save_path_);
            auto const download_path = text(payload, "downloadPath");
            params.save_path = boolean(payload, "useDownloadPath") && !download_path.empty() ? download_path : complete_path;
            params.flags |= lt::torrent_flags::duplicate_is_error;
            if (boolean(payload, "startTorrent", true)) params.flags &= ~lt::torrent_flags::paused;
            else params.flags |= lt::torrent_flags::paused;
            if (queueing_enabled_) params.flags |= lt::torrent_flags::auto_managed;
            else params.flags &= ~lt::torrent_flags::auto_managed;
            if (boolean(payload, "sequentialDownload")) params.flags |= lt::torrent_flags::sequential_download;
            if (!pex_enabled_) params.flags |= lt::torrent_flags::disable_pex;
            if (boolean(payload, "skipChecking")) params.flags |= lt::torrent_flags::seed_mode;
            if (preallocate_all_) params.storage_mode = lt::storage_mode_allocate;
            params.max_connections = max_connections_per_torrent_;
            params.max_uploads = max_uploads_per_torrent_;
        }

        void add(json::object const& payload)
        {
            std::vector<lt::torrent_handle> added;
            for (auto const& path : strings(payload, "torrentFiles"))
            {
                lt::error_code error;
                lt::add_torrent_params params;
                params.ti = std::make_shared<lt::torrent_info>(path, error);
                if (error) throw std::runtime_error("Unable to parse torrent: " + error.message());
                configure(params, payload);
                added.push_back(add_one(std::move(params), payload));
            }
            for (auto const& uri : strings(payload, "urls"))
            {
                if (!std::string_view(uri).starts_with("magnet:?"))
                    throw std::runtime_error("EngineHost must fetch HTTP torrent URLs before adding them.");
                lt::error_code error;
                auto params = lt::parse_magnet_uri(uri, error);
                if (error) throw std::runtime_error("Unable to parse magnet: " + error.message());
                configure(params, payload);
                added.push_back(add_one(std::move(params), payload));
            }
            if (!added.empty()) save_resume_files(std::move(added), false);
        }

        lt::torrent_handle add_one(lt::add_torrent_params params, json::object const& payload)
        {
            lt::error_code error;
            auto handle = session_.add_torrent(std::move(params), error);
            if (error) throw std::runtime_error("Unable to add torrent: " + error.message());
            persist_torrent_file(handle);
            auto& extra = state(handle);
            extra.category = text(payload, "category");
            extra.download_path = text(payload, "downloadPath");
            extra.complete_path = text(payload, "savePath", handle.status().save_path);
            extra.first_last = boolean(payload, "firstLastPiecePriority");
            extra.automatic_tmm = boolean(payload, "automaticTorrentManagement") || auto_tmm_enabled_;
            for (auto const& tag : split(text(payload, "tags"))) { extra.tags.insert(tag); global_tags_.insert(tag); }
            if (payload.contains("uploadLimit") && !payload.at("uploadLimit").is_null()) handle.set_upload_limit(static_cast<int>(integer(payload, "uploadLimit")));
            if (payload.contains("downloadLimit") && !payload.at("downloadLimit").is_null()) handle.set_download_limit(static_cast<int>(integer(payload, "downloadLimit")));
            set_first_last(handle, extra.first_last);
            return handle;
        }

        void persist_torrent_file(lt::torrent_handle const& handle)
        {
            auto const info = handle.torrent_file();
            if (!info) return;
            lt::add_torrent_params params;
            params.ti = std::make_shared<lt::torrent_info>(*info);
            for (auto const& tracker : handle.trackers()) params.trackers.push_back(tracker.url);
            write_atomic(torrent_root_ / (primary_hash(handle.info_hashes()) + ".torrent"),
                lt::write_torrent_file_buf(params, lt::write_flags::allow_missing_piece_layer));
        }

        void remove(json::object const& payload)
        {
            for (auto const& handle : selected(text(payload, "hashes")))
            {
                auto const hash = primary_hash(handle.info_hashes());
                session_.remove_torrent(handle, boolean(payload, "deleteFiles") ? lt::session::delete_files : lt::remove_flags_t{});
                states_.erase(hash);
                std::error_code ignored;
                fs::remove(resume_root_ / (hash + ".fastresume"), ignored);
                fs::remove(torrent_root_ / (hash + ".torrent"), ignored);
            }
        }

        static void set_first_last(lt::torrent_handle const& handle, bool enabled)
        {
            auto const info = handle.torrent_file();
            if (!info || info->num_pieces() == 0) return;
            handle.piece_priority(lt::piece_index_t{ 0 }, enabled ? lt::top_priority : lt::default_priority);
            if (info->num_pieces() > 1)
                handle.piece_priority(lt::piece_index_t{ info->num_pieces() - 1 }, enabled ? lt::top_priority : lt::default_priority);
        }

        void command(json::object const& payload)
        {
            std::vector<lt::torrent_handle> stopped;
            for (auto const& handle : selected(text(payload, "hashes")))
            {
                switch (integer(payload, "command"))
                {
                case 0: handle.resume(); state(handle).force_start = false; break;
                case 1: handle.pause(); stopped.push_back(handle); break;
                case 2:
                    if (handle.status().flags & lt::torrent_flags::paused)
                    {
                        handle.unset_flags(lt::torrent_flags::auto_managed);
                        handle.set_flags(lt::torrent_flags::upload_mode);
                        handle.resume();
                        pause_after_recheck_.insert(primary_hash(handle.info_hashes()));
                    }
                    handle.force_recheck();
                    break;
                case 3: handle.force_reannounce(); break;
                case 4: handle.queue_position_up(); break;
                case 5: handle.queue_position_down(); break;
                case 6: handle.queue_position_top(); break;
                case 7: handle.queue_position_bottom(); break;
                case 8:
                    if (handle.flags() & lt::torrent_flags::sequential_download) handle.unset_flags(lt::torrent_flags::sequential_download);
                    else handle.set_flags(lt::torrent_flags::sequential_download);
                    break;
                case 9: state(handle).first_last = !state(handle).first_last; set_first_last(handle, state(handle).first_last); break;
                default: throw std::runtime_error("Unknown torrent command.");
                }
            }
            if (!stopped.empty()) save_resume_files(std::move(stopped), false);
        }

        void action(json::object const& payload)
        {
            auto const name = text(payload, "action");
            auto const parameters = string_parameters(payload);
            if (name == "createCategory" || name == "editCategory")
            {
                categories_[text(parameters, "category")] = { text(parameters, "savePath"), text(parameters, "downloadPath") };
                return;
            }
            if (name == "removeCategories")
            {
                for (auto const& value : split(text(parameters, "categories")))
                {
                    categories_.erase(value);
                    for (auto& [_, extra] : states_) if (extra.category == value) extra.category.clear();
                }
                return;
            }
            if (name == "createTags") { for (auto const& value : split(text(parameters, "tags"))) global_tags_.insert(value); return; }
            if (name == "deleteTags")
            {
                for (auto const& value : split(text(parameters, "tags")))
                {
                    global_tags_.erase(value);
                    for (auto& [_, extra] : states_) extra.tags.erase(value);
                }
                return;
            }
            auto const hash_key = parameters.contains("hashes") ? "hashes" : "hash";
            if (name == "setLocation")
            {
                auto const location = utf8_path(text(parameters, "location"));
                if (location.empty() || !location.is_absolute())
                    throw std::runtime_error("Torrent location must be an absolute directory path.");
                // Validate all selected torrents before starting any asynchronous move.
                for (auto const& handle : selected(text(parameters, hash_key)))
                    if (!state(handle).pending_location.empty() || handle.status().moving_storage)
                        throw std::runtime_error("A storage move is already in progress.");
                fs::create_directories(location);
            }
            for (auto const& handle : selected(text(parameters, hash_key)))
            {
                auto& extra = state(handle);
                if (name == "setForceStart")
                {
                    extra.force_start = boolean(parameters, "value");
                    if (extra.force_start) { handle.unset_flags(lt::torrent_flags::auto_managed); handle.resume(); }
                    else if (queueing_enabled_) handle.set_flags(lt::torrent_flags::auto_managed);
                }
                else if (name == "setSuperSeeding")
                {
                    if (boolean(parameters, "value")) handle.set_flags(lt::torrent_flags::super_seeding);
                    else handle.unset_flags(lt::torrent_flags::super_seeding);
                }
                else if (name == "setCategory")
                {
                    extra.category = text(parameters, "category");
                    auto const category = categories_.find(extra.category);
                    if (extra.automatic_tmm && category != categories_.end())
                    {
                        auto const& [save_path, download_path] = category->second;
                        if (!save_path.empty())
                        {
                            extra.complete_path = save_path;
                            handle.move_storage(save_path);
                        }
                        extra.download_path = download_path;
                    }
                }
                else if (name == "addTags") for (auto const& value : split(text(parameters, "tags"))) { extra.tags.insert(value); global_tags_.insert(value); }
                else if (name == "removeTags") for (auto const& value : split(text(parameters, "tags"))) extra.tags.erase(value);
                else if (name == "setLocation")
                {
                    extra.pending_location = text(parameters, "location");
                    handle.move_storage(extra.pending_location);
                }
                else if (name == "setDownloadLimit") handle.set_download_limit(static_cast<int>(integer(parameters, "limit")));
                else if (name == "setUploadLimit") handle.set_upload_limit(static_cast<int>(integer(parameters, "limit")));
                else if (name == "setShareLimits") { extra.ratio_limit = number(parameters, "ratioLimit", -1); extra.seeding_time_limit = static_cast<int>(integer(parameters, "seedingTimeLimit", -1)); extra.inactive_seeding_time_limit = static_cast<int>(integer(parameters, "inactiveSeedingTimeLimit", -1)); }
                else if (name == "rename") extra.display_name = text(parameters, "name");
                else if (name == "filePrio") set_file_priority(handle, parameters);
                else if (name == "addTrackers") add_trackers(handle, text(parameters, "urls"));
                else if (name == "removeTrackers") remove_trackers(handle, text(parameters, "urls"));
                else if (name == "addWebSeeds") for (auto const& value : split(text(parameters, "urls"))) handle.add_url_seed(value);
                else if (name == "removeWebSeeds") for (auto const& value : split(text(parameters, "urls"))) handle.remove_url_seed(value);
                else throw std::runtime_error("Unknown torrent action: " + name);
            }
        }

        static void set_file_priority(lt::torrent_handle const& handle, json::object const& parameters)
        {
            auto values = handle.get_file_priorities();
            auto const priority = native_priority(static_cast<int>(integer(parameters, "priority", 1)));
            for (auto const& item : split(text(parameters, "id")))
            {
                auto const index = std::stoi(item);
                if (index >= 0 && static_cast<std::size_t>(index) < values.size()) values[static_cast<std::size_t>(index)] = priority;
            }
            handle.prioritize_files(values);
        }

        static void add_trackers(lt::torrent_handle const& handle, std::string const& urls)
        {
            auto values = handle.trackers();
            int tier = values.empty() ? 0 : values.back().tier + 1;
            for (auto const& url : split(urls))
            {
                lt::announce_entry entry(url);
                entry.tier = static_cast<std::uint8_t>(std::min(tier++, 255));
                values.push_back(std::move(entry));
            }
            handle.replace_trackers(values);
        }

        static void remove_trackers(lt::torrent_handle const& handle, std::string const& urls)
        {
            auto values = handle.trackers();
            auto const removed = split(urls);
            std::erase_if(values, [&](lt::announce_entry const& entry) { return std::find(removed.begin(), removed.end(), entry.url) != removed.end(); });
            handle.replace_trackers(values);
        }

        json::array export_bytes(json::object const& payload) const
        {
            auto const handle = require(text(payload, "hash"));
            auto const info = handle.torrent_file();
            if (!info) throw std::runtime_error("Torrent metadata is not available.");
            lt::add_torrent_params params;
            params.ti = std::make_shared<lt::torrent_info>(*info);
            for (auto const& tracker : handle.trackers()) params.trackers.push_back(tracker.url);
            auto const bytes = lt::write_torrent_file_buf(params, lt::write_flags::allow_missing_piece_layer);
            json::array result;
            result.reserve(bytes.size());
            for (unsigned char value : bytes) result.push_back(value);
            return result;
        }

        json::object parse_metadata(json::object const& payload) const
        {
            lt::error_code error;
            lt::torrent_info info(text(payload, "torrentFilePath"), error);
            if (error) throw std::runtime_error("Unable to parse torrent: " + error.message());
            json::array file_values;
            for (lt::file_index_t index{ 0 }; index < info.num_files(); ++index)
                file_values.push_back({ { "index", static_cast<int>(index) }, { "name", info.files().file_path(index) }, { "size", info.files().file_size(index) } });
            return { { "name", info.name() }, { "hash", primary_hash(info.info_hashes()) },
                { "infohash_v1", v1_hash(info.info_hashes()) }, { "infohash_v2", v2_hash(info.info_hashes()) },
                { "size", info.total_size() }, { "piece_size", info.piece_length() }, { "pieces", info.num_pieces() },
                { "private", info.priv() }, { "files", std::move(file_values) } };
        }

        json::object metadata(json::object const& payload) const
        {
            auto const info = require(text(payload, "hash")).torrent_file();
            if (!info) throw std::runtime_error("Torrent metadata is not available yet.");
            json::array file_values;
            for (lt::file_index_t index{ 0 }; index < info->num_files(); ++index)
                file_values.push_back({ { "index", static_cast<int>(index) }, { "name", info->files().file_path(index) }, { "size", info->files().file_size(index) } });
            return { { "name", info->name() }, { "hash", primary_hash(info->info_hashes()) },
                { "infohash_v1", v1_hash(info->info_hashes()) }, { "infohash_v2", v2_hash(info->info_hashes()) },
                { "size", info->total_size() }, { "piece_size", info->piece_length() }, { "pieces", info->num_pieces() },
                { "private", info->priv() }, { "files", std::move(file_values) } };
        }

        void restore_app_state(json::object const& payload)
        {
            if (auto const* torrent_values = payload.if_contains("torrents"); torrent_values != nullptr && torrent_values->is_array())
            {
                for (auto const& value : torrent_values->as_array())
                {
                    if (!value.is_object()) continue;
                    auto const& item = value.as_object();
                    try
                    {
                        auto handle = require(text(item, "hash"));
                        auto const desired_path = !text(item, "downloadPath").empty()
                            ? text(item, "downloadPath") : text(item, "savePath");
                        auto const needs_recheck = boolean(item, "needsRecheck");
                        auto const loaded_from_metadata_fallback = !desired_path.empty()
                            && utf8_path(handle.status().save_path).lexically_normal() == root_.lexically_normal()
                            && utf8_path(desired_path).lexically_normal() != root_.lexically_normal();
                        if ((needs_recheck || loaded_from_metadata_fallback) && !desired_path.empty()
                            && utf8_path(handle.status().save_path).lexically_normal() == root_.lexically_normal())
                        {
                            auto const hash = primary_hash(handle.info_hashes());
                            session_.remove_torrent(handle);
                            lt::error_code error;
                            lt::add_torrent_params params;
                            params.ti = std::make_shared<lt::torrent_info>(path_text(torrent_root_ / (hash + ".torrent")), error);
                            if (error) throw std::runtime_error(error.message());
                            params.save_path = desired_path;
                            params.flags |= lt::torrent_flags::paused;
                            params.flags &= ~lt::torrent_flags::auto_managed;
                            handle = session_.add_torrent(std::move(params), error);
                            if (error) throw std::runtime_error(error.message());
                        }
                        auto& extra = state(handle);
                        extra.category = text(item, "category");
                        extra.display_name = text(item, "displayName");
                        extra.download_path = text(item, "downloadPath");
                        extra.complete_path = text(item, "savePath");
                        extra.first_last = boolean(item, "firstLast");
                        extra.force_start = boolean(item, "forceStart");
                        extra.automatic_tmm = boolean(item, "automaticTmm");
                        extra.ratio_limit = number(item, "ratioLimit", -1);
                        extra.seeding_time_limit = static_cast<int>(integer(item, "seedingTimeLimit", -1));
                        extra.inactive_seeding_time_limit = static_cast<int>(integer(item, "inactiveSeedingTimeLimit", -1));
                        extra.queue_position = static_cast<int>(integer(item, "queuePosition", -1));
                        extra.needs_recheck = needs_recheck || loaded_from_metadata_fallback;
                        extra.tags.clear();
                        for (auto const& tag : split(text(item, "tags"))) extra.tags.insert(tag);
                        if (extra.queue_position >= 0)
                            handle.queue_position_set(lt::queue_position_t{ extra.queue_position });
                    }
                    catch (std::exception const&) {}
                }
            }
            if (auto const* category_values = payload.if_contains("categories"); category_values != nullptr && category_values->is_array())
            {
                categories_.clear();
                for (auto const& value : category_values->as_array())
                {
                    if (!value.is_object()) continue;
                    auto const& item = value.as_object();
                    categories_[text(item, "name")] = { text(item, "savePath"), text(item, "downloadPath") };
                }
            }
            if (auto const* tag_values = payload.if_contains("tags"); tag_values != nullptr && tag_values->is_array())
            {
                global_tags_.clear();
                for (auto const& value : tag_values->as_array())
                    if (value.is_string()) global_tags_.insert(std::string(value.as_string()));
            }
        }

        void apply_global_limits()
        {
            auto settings = session_.get_settings();
            auto const down = alternative_limits_ ? alternative_download_limit_ : download_limit_;
            auto const up = alternative_limits_ ? alternative_upload_limit_ : upload_limit_;
            settings.set_int(lt::settings_pack::download_rate_limit, static_cast<int>(std::min<std::int64_t>(down, std::numeric_limits<int>::max())));
            settings.set_int(lt::settings_pack::upload_rate_limit, static_cast<int>(std::min<std::int64_t>(up, std::numeric_limits<int>::max())));
            session_.apply_settings(settings);
        }

        void apply_settings(json::object const& values)
        {
            auto settings = session_.get_settings();
            settings.set_bool(lt::settings_pack::enable_dht, boolean(values, "dht", true));
            settings.set_bool(lt::settings_pack::enable_lsd, boolean(values, "lsd", true));
            settings.set_bool(lt::settings_pack::enable_upnp, boolean(values, "upnp", true));
            settings.set_bool(lt::settings_pack::enable_natpmp, boolean(values, "upnp", true));
            settings.set_bool(lt::settings_pack::anonymous_mode, boolean(values, "anonymous_mode"));
            auto const protocol = integer(values, "bittorrent_protocol", 0);
            settings.set_bool(lt::settings_pack::enable_incoming_tcp, protocol != 2);
            settings.set_bool(lt::settings_pack::enable_outgoing_tcp, protocol != 2);
            settings.set_bool(lt::settings_pack::enable_incoming_utp, protocol != 1);
            settings.set_bool(lt::settings_pack::enable_outgoing_utp, protocol != 1);
            auto const port = integer(values, "listen_port", 0);
            if (boolean(values, "random_port", true))
                settings.set_str(lt::settings_pack::listen_interfaces, "0.0.0.0:0,[::]:0");
            else if (port > 0 && port <= 65535)
                settings.set_str(lt::settings_pack::listen_interfaces, "0.0.0.0:" + std::to_string(port) + ",[::]:" + std::to_string(port));
            auto const connections = integer(values, "max_connec", -1);
            settings.set_int(lt::settings_pack::connections_limit,
                connections < 0 ? std::numeric_limits<int>::max() : static_cast<int>(connections));
            auto const slots = integer(values, "max_uploads", -1);
            settings.set_int(lt::settings_pack::unchoke_slots_limit,
                slots < 0 ? std::numeric_limits<int>::max() : static_cast<int>(slots));
            settings.set_int(lt::settings_pack::active_downloads, static_cast<int>(integer(values, "max_active_downloads", 3)));
            settings.set_int(lt::settings_pack::active_seeds, static_cast<int>(integer(values, "max_active_uploads", 3)));
            queueing_enabled_ = boolean(values, "queueing_enabled");
            auto_tmm_enabled_ = boolean(values, "auto_tmm_enabled");
            settings.set_int(lt::settings_pack::aio_threads, static_cast<int>(integer(values, "async_io_threads", 4)));
            settings.set_int(lt::settings_pack::alert_queue_size, 1000);
            auto const disk_cache_mib = integer(values, "disk_cache", -1);
            if (disk_cache_mib >= 0)
                settings.set_int(lt::settings_pack::max_queued_disk_bytes,
                    static_cast<int>(std::min<std::int64_t>(disk_cache_mib * 1024 * 1024, std::numeric_limits<int>::max())));

            auto const encryption = integer(values, "encryption", 0);
            settings.set_int(lt::settings_pack::in_enc_policy,
                encryption == 1 ? lt::settings_pack::pe_forced : encryption == 2 ? lt::settings_pack::pe_disabled : lt::settings_pack::pe_enabled);
            settings.set_int(lt::settings_pack::out_enc_policy,
                encryption == 1 ? lt::settings_pack::pe_forced : encryption == 2 ? lt::settings_pack::pe_disabled : lt::settings_pack::pe_enabled);

            auto const proxy_name = text(values, "proxy_type", "None");
            int proxy_type = lt::settings_pack::none;
            if (proxy_name == "SOCKS4") proxy_type = lt::settings_pack::socks4;
            else if (proxy_name == "SOCKS5") proxy_type = lt::settings_pack::socks5;
            else if (proxy_name == "SOCKS5_PW") proxy_type = lt::settings_pack::socks5_pw;
            else if (proxy_name == "HTTP") proxy_type = lt::settings_pack::http;
            else if (proxy_name == "HTTP_PW") proxy_type = lt::settings_pack::http_pw;
            else if (proxy_name == "I2P") proxy_type = lt::settings_pack::i2p_proxy;
            settings.set_int(lt::settings_pack::proxy_type, proxy_type);
            settings.set_str(lt::settings_pack::proxy_hostname, text(values, "proxy_ip"));
            settings.set_int(lt::settings_pack::proxy_port, static_cast<int>(integer(values, "proxy_port")));
            auto const proxy_auth = boolean(values, "proxy_auth_enabled");
            settings.set_str(lt::settings_pack::proxy_username, proxy_auth ? text(values, "proxy_username") : "");
            settings.set_str(lt::settings_pack::proxy_password, proxy_auth ? text(values, "proxy_password") : "");
            settings.set_bool(lt::settings_pack::proxy_hostnames, boolean(values, "proxy_hostname_lookup"));
            settings.set_bool(lt::settings_pack::proxy_peer_connections,
                boolean(values, "proxy_peer_connections", boolean(values, "proxy_bittorrent", true)));
            settings.set_bool(lt::settings_pack::proxy_tracker_connections, boolean(values, "proxy_bittorrent", true));
            settings.set_bool(lt::settings_pack::apply_ip_filter_to_trackers, boolean(values, "ip_filter_trackers"));
            if (boolean(values, "i2p_enabled"))
            {
                settings.set_str(lt::settings_pack::i2p_hostname, text(values, "i2p_address", "127.0.0.1"));
                settings.set_int(lt::settings_pack::i2p_port, static_cast<int>(integer(values, "i2p_port", 7656)));
                settings.set_bool(lt::settings_pack::allow_i2p_mixed, boolean(values, "i2p_mixed_mode"));
            }
            else settings.set_str(lt::settings_pack::i2p_hostname, "");

            session_.apply_settings(settings);
            apply_ip_filter(values);
            default_save_path_ = text(values, "save_path", path_text(root_));
            download_limit_ = integer(values, "dl_limit") * 1024;
            upload_limit_ = integer(values, "up_limit") * 1024;
            alternative_download_limit_ = integer(values, "alt_dl_limit") * 1024;
            alternative_upload_limit_ = integer(values, "alt_up_limit") * 1024;
            alternative_limits_ = boolean(values, "use_alt_speed_limits", alternative_limits_);
            preallocate_all_ = boolean(values, "preallocate_all");
            recheck_completed_ = boolean(values, "recheck_completed_torrents");
            max_connections_per_torrent_ = static_cast<int>(integer(values, "max_connec_per_torrent", -1));
            max_uploads_per_torrent_ = static_cast<int>(integer(values, "max_uploads_per_torrent", -1));
            apply_global_limits();

            pex_enabled_ = boolean(values, "pex", true);
            for (auto const& handle : session_.get_torrents())
            {
                if (pex_enabled_) handle.unset_flags(lt::torrent_flags::disable_pex);
                else handle.set_flags(lt::torrent_flags::disable_pex);
                if (queueing_enabled_ && !state(handle).force_start) handle.set_flags(lt::torrent_flags::auto_managed);
                else handle.unset_flags(lt::torrent_flags::auto_managed);
                handle.set_max_connections(max_connections_per_torrent_);
                handle.set_max_uploads(max_uploads_per_torrent_);
            }

            for (auto const mapping : remote_api_mappings_) session_.delete_port_mapping(mapping);
            remote_api_mappings_.clear();
            auto const remote_api_port = static_cast<int>(integer(values, "web_ui_port"));
            if (boolean(values, "web_ui_upnp") && remote_api_port > 0 && remote_api_port <= 65535)
                remote_api_mappings_ = session_.add_port_mapping(lt::portmap_protocol::tcp, remote_api_port, remote_api_port);
        }

        void apply_ip_filter(json::object const& values)
        {
            lt::ip_filter filter;
            auto add_rule = [&](std::string first_text, std::string last_text)
            {
                lt::error_code first_error;
                lt::error_code last_error;
                auto const first = lt::make_address(peer_address(std::move(first_text)), first_error);
                auto const last = lt::make_address(peer_address(std::move(last_text)), last_error);
                if (!first_error && !last_error && first.is_v4() == last.is_v4())
                    filter.add_rule(first, last, lt::ip_filter::blocked);
            };
            for (auto const& address : split(text(values, "banned_IPs"))) add_rule(address, address);
            if (boolean(values, "ip_filter_enabled"))
            {
                auto const path = text(values, "ip_filter_path");
                std::ifstream input(utf8_path(path));
                std::string line;
                while (std::getline(input, line))
                {
                    auto const comment = line.find('#');
                    if (comment != std::string::npos) line.resize(comment);
                    auto const description = line.find(':');
                    if (description != std::string::npos && line.find('.') != std::string::npos && description < line.find('.')) line.erase(0, description + 1);
                    auto const separator = line.find('-');
                    if (separator == std::string::npos) add_rule(line, line);
                    else add_rule(line.substr(0, separator), line.substr(separator + 1));
                }
            }
            session_.set_ip_filter(std::move(filter));
        }

        fs::path root_;
        fs::path torrent_root_;
        fs::path resume_root_;
        mutable std::mutex mutex_;
        lt::session session_;
        std::unordered_map<std::string, app_state> states_;
        std::unordered_map<std::string, std::pair<std::string, std::string>> categories_;
        std::set<std::string> global_tags_;
        json::array storage_errors_;
        bool storage_changed_ = false;
        bool alternative_limits_ = false;
        std::int64_t download_limit_ = 0;
        std::int64_t upload_limit_ = 0;
        std::int64_t alternative_download_limit_ = 0;
        std::int64_t alternative_upload_limit_ = 0;
        bool resume_saved_ = false;
        bool queueing_enabled_ = false;
        bool auto_tmm_enabled_ = false;
        bool pex_enabled_ = true;
        bool preallocate_all_ = false;
        bool recheck_completed_ = false;
        int max_connections_per_torrent_ = -1;
        int max_uploads_per_torrent_ = -1;
        std::set<std::string> rechecked_completed_;
        std::unordered_set<std::string> pause_after_recheck_;
        std::vector<lt::port_mapping_t> remote_api_mappings_;
        int const dht_nodes_metric_ = lt::find_metric_idx("dht.dht_nodes");
        int dht_nodes_ = 0;
        std::chrono::steady_clock::time_point last_stats_request_{};
        std::string default_save_path_;
    };

    engine* create_engine(std::string const& data_root) { return new engine(data_root); }
    void destroy_engine(engine* instance) noexcept { delete instance; }
    std::string invoke_engine(engine& instance, std::string const& method, std::string const& payload)
    {
        return instance.invoke(method, payload);
    }

    std::vector<char> create_torrent_data(std::string const& payload_json, std::function<bool(int, int)> const& progress)
    {
        auto const payload_value = json::parse(payload_json.empty() ? "{}" : payload_json);
        auto const& payload = payload_value.as_object();
        auto const source_text = text(payload, "sourcePath");
        if (source_text.empty()) throw std::invalid_argument("sourcePath is required");
        auto const source = utf8_path(source_text);
        if (!fs::exists(source)) throw std::invalid_argument("The torrent source does not exist: " + source_text);

        lt::create_flags_t flags{};
        auto const version = text(payload, "torrentVersion", "hybrid");
        if (version == "v1") flags |= lt::create_torrent::v1_only;
        else if (version == "v2") flags |= lt::create_torrent::v2_only;
        else if (version != "hybrid") throw std::invalid_argument("torrentVersion must be v1, v2 or hybrid");

        lt::file_storage storage;
        lt::add_files(storage, path_text(source), [](std::string const&) { return true; }, flags);
        if (storage.num_files() == 0) throw std::runtime_error("The torrent source does not contain files");
        auto const requested_piece_size = static_cast<int>(integer(payload, "pieceSize"));
        lt::create_torrent torrent(storage, requested_piece_size, flags);
        if (boolean(payload, "isPrivate")) torrent.set_priv(true);
        auto const comment = text(payload, "comment");
        if (!comment.empty()) torrent.set_comment(comment.c_str());
        torrent.set_creator("WinBitTorrent/1.0");

        auto const tracker_text = text(payload, "trackers");
        int tier = 0;
        for (auto const& tracker : split(tracker_text))
        {
            if (tracker == "-") ++tier;
            else torrent.add_tracker(tracker, tier);
        }

        auto const total = torrent.num_pieces();
        auto const base = source.has_parent_path() ? source.parent_path() : fs::current_path();
        lt::set_piece_hashes(torrent, path_text(base), [&](lt::piece_index_t const piece)
        {
            auto const completed = static_cast<int>(piece) + 1;
            if (!progress(completed, total)) throw std::runtime_error("Torrent creation was cancelled");
        });
        if (!progress(total, total)) throw std::runtime_error("Torrent creation was cancelled");

        std::vector<char> result;
        lt::bencode(std::back_inserter(result), torrent.generate());
        return result;
    }
}
