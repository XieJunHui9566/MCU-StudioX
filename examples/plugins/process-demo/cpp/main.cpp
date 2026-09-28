#include <cstdint>
#include <iostream>
#include <set>
#include <stdexcept>
#include <string>
#include <nlohmann/json.hpp>

using Json = nlohmann::json;

// 原生 EXE 仅通过标准输入输出协议贡献数据，不引用 StudioX Desktop。
class Plugin {
public:
    void run() {
        Json message;
        while (receive(message)) {
            dispatch(message);
        }
    }

private:
    static constexpr std::size_t max_line_bytes = 1024 * 1024;
    std::uint64_t next_id = 0;
    std::set<std::string> cancelled;
    std::set<std::string> active;

    static void send(const Json& message) {
        auto text = message.dump();
        if (text.size() > max_line_bytes) {
            throw std::runtime_error("Protocol output exceeds limit");
        }
        std::cout << text << '\n' << std::flush;
        if (!std::cout) {
            throw std::runtime_error("Host protocol closed");
        }
    }

    static bool receive(Json& message) {
        std::string line;
        char character;
        while (std::cin.get(character)) {
            if (character == '\n') {
                break;
            }
            if (line.size() >= max_line_bytes) {
                throw std::runtime_error("Protocol input exceeds limit");
            }
            line += character;
        }
        if (line.empty() && !std::cin) {
            return false;
        }
        message = Json::parse(line);
        if (message.value("protocolVersion", 0) != 2) {
            throw std::runtime_error("Unsupported protocol version");
        }
        return true;
    }

    static void event(const std::string& method, const Json& payload) {
        send({{"protocolVersion", 2}, {"kind", "event"}, {"method", method}, {"payload", payload}});
    }

    static Json panel(const Json& project) {
        return {{"id", "overview"}, {"title", "C++ project overview"}, {"widgets", Json::array({
            {{"id", "project"}, {"kind", "text"}, {"label", "project_info"}, {"value", project.dump()}},
            {{"id", "refresh"}, {"kind", "button"}, {"label", "Refresh"}, {"commandId", "refresh"}}
        })}};
    }

    Json host_call(const std::string& current, const std::string& tool) {
        const auto id = "cpp-host-" + std::to_string(++next_id);
        send({{"protocolVersion", 2}, {"kind", "request"}, {"requestId", id}, {"method", "hostCall"},
            {"payload", {{"tool", tool}, {"arguments", Json::object()}}}});
        Json message;
        while (receive(message)) {
            if (message.value("kind", "") == "response" && message.value("requestId", "") == id) {
                if (cancelled.erase(current) != 0) {
                    throw std::runtime_error("PLUGIN_CANCELLED");
                }
                if (message.contains("errorCode") && !message["errorCode"].is_null()) {
                    throw std::runtime_error(message.value("error", "Host tool failed"));
                }
                return message.at("payload");
            }
            // 等待宿主工具时仍处理取消和其他协议消息，避免双向调用互相等待读取。
            dispatch(message);
        }
        throw std::runtime_error("Host closed protocol");
    }

    Json request(const std::string& method, const Json& message) {
        if (method == "describe") {
            return {{"commands", Json::array({{{"id", "refresh"}, {"title", "Refresh C++ overview"}, {"placement", "tools"}}})},
                {"panels", Json::array({panel({{"status", "Waiting for host snapshot"}})})},
                {"agentTools", Json::array({{{"id", "overview"}, {"description", "Read current project through the approved host broker."},
                    {"inputSchema", {{"type", "object"}, {"properties", Json::object()}, {"additionalProperties", false}}}}})}};
        }
        if (method == "activate") {
            event("log", {{"level", "info"}, {"message", "C++ native plugin activated"}});
            return Json::object();
        }
        if (method == "deactivate") {
            return Json::object();
        }
        if (method == "invoke") {
            const auto& payload = message.at("payload");
            const auto kind = payload.value("kind", "");
            const auto id = payload.value("id", "");
            if ((kind == "command" && id == "refresh") || (kind == "agentTool" && id == "overview")) {
                auto project = host_call(message.at("requestId").get<std::string>(), "project_info");
                event("panel", panel(project));
                return project;
            }
            throw std::runtime_error("Unregistered contribution");
        }
        throw std::runtime_error("Unsupported method");
    }

    void dispatch(const Json& message) {
        const auto kind = message.value("kind", "");
        const auto id = message.value("requestId", "");
        if (kind == "cancel") {
            if (active.contains(id)) {
                cancelled.insert(id);
            }
            return;
        }
        if (kind == "response") {
            return;
        }
        if (kind != "request" || id.empty()) {
            throw std::runtime_error("Unexpected protocol message");
        }
        if (active.size() >= 32 || !active.insert(id).second) {
            throw std::runtime_error("Duplicate or too many requests");
        }
        Json response = {{"protocolVersion", 2}, {"kind", "response"}, {"requestId", id}};
        try {
            response["payload"] = request(message.value("method", ""), message);
        } catch (const std::exception& error) {
            response["errorCode"] = std::string(error.what()) == "PLUGIN_CANCELLED" ? "PLUGIN_CANCELLED" : "CPP_PLUGIN";
            response["error"] = error.what();
        }
        active.erase(id);
        cancelled.erase(id);
        send(response);
    }
};

int main() {
    try {
        Plugin().run();
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
    return 0;
}
