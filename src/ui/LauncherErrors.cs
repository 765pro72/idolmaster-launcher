using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

internal static class LauncherErrors
{
    const string Report = "保留出错的操作、时间和提示，连同该次日志反馈给维护者。";
    const string StopLocal = "先关闭游戏，再在「更多」中点「停止本地服务」，然后重试。";
    const string CheckPackage = "把完整整合包解压到可写的新目录，重新选择游戏目录后再试。";
    const string CheckDmm = "先停止学园本地服务并恢复平时使用的系统代理。用 DMM 正常启动所选安装，关闭游戏后再更新。";
    const string CheckNetwork = "确认平时使用的代理已连接，启动器配置的上游地址和端口可用，再试一次。";
    const string CheckAdapter = "在百万现场页检查模拟器中游戏的版本，先执行「更新游戏适配」，完成后再重复原操作。";
    const string CheckAssets = "先检查资源包版本和完整性。缺少资源时补齐对应包，再重试下载或播放。";
    const string CheckScsp = "确认官方游戏目录完整，关闭旧会话后重新准备外部工作目录，再重试。";
    const string CheckCgss = "检查所选灰姑娘女孩目录及本地服务日志。确认游戏和整合包都已完整解压，再试一次。";

    static string Instruction(string reason, string fix)
    {
        return reason + "。处理方法：" + fix;
    }

    sealed class Rule
    {
        internal readonly Regex Pattern;
        internal readonly string Text;

        internal Rule(string pattern, string reason, string fix)
        {
            Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Text = Instruction(reason, fix);
        }
    }

    static readonly Rule[] Rules = {
        new Rule(@"Official System/Check rejected.*\bcode\s+2004\b|(?:x-error-code|错误码)\s*[:=]?\s*2004\b",
                 "官方要求升级学园客户端（官方错误码 2004）", "用 DMM 更新学园客户端，进入标题后关闭游戏，再用启动器点「检查并更新内容」。"),
        new Rule(@"Official System/Check rejected", "官方拒绝当前客户端初始化", CheckDmm + "如果日志提示版本过旧，先从 DMM 更新游戏。"),
        new Rule(@"Protocol changed|compatibility review required|master envelope differs|Unsupported client metadata|protocol hash format",
                 "当前客户端的协议或主表格式不兼容", "确认 DMM 已更新客户端，然后使用与该版本匹配的整合包。把协议提示和客户端版本一并反馈。"),
        new Rule(@"An update port belongs to another program|\bEADDRINUSE\b|address already in use|端口.*已被|端口.*占用",
                 "所需端口已被其他程序占用", StopLocal + "如果提示仍在，确认占用端口的是旧整合包还是其他软件，再关闭相应程序。"),
        new Rule(@"Another content update is in progress|content update is running|已有同步任务|A global stop is still running",
                 "已有同类任务在运行", "等当前任务结束。若是学园更新卡住，通过「停止本地服务」中断后再点一次更新。"),
        new Rule(@"online capture marker already exists", "上次在线更新留下了未清理的状态", StopLocal),
        new Rule(@"Close the game before updating|游戏已经在运行|请先关闭学园游戏",
                 "学园游戏仍在运行", "先正常退出游戏，确认游戏窗口已关闭，再重复刚才的操作。"),
        new Rule(@"DMM login belongs to a different game installation",
                 "DMM 上次启动的是另一份游戏", "在启动器重新选择正确的游戏目录。用 DMM 启动同一份游戏一次，关闭游戏后再更新。"),
        new Rule(@"缺少真实 onetime_token|没能从 DMMGamePlayer 日志里取到登录凭据|no official.+(?:credential|ticket)|(?:login|launch) ticket.*(?:missing|expired)",
                 "DMM 登录票据缺失或不可用", CheckDmm),
        new Rule(@"TAP TO START.*Master/Get|未收到 Master/Get|没有抓到.*Master/Get|No fresh Master/Get|Timed out waiting for.*Master/Get",
                 "更新游戏没有返回主表清单", "确认更新时出现游戏窗口，在标题页点「TAP TO START」并等到大厅。如果游戏弹初始化失败，先处理该弹窗，再重试更新。"),
        new Rule(@"Saved local proxy is not listening|Mixed launcher and external proxies|Saved proxy points back|invalid saved proxy|Proxy restore:|proxy setup failed",
                 "系统代理恢复或配置异常", "先点「停止本地服务」。打开 Windows 的代理设置，恢复平时使用的代理；如果使用 Clash，请重新启用它的系统代理，再打开 DMM。"),
        new Rule(@"证书.*(?:未|失败)|(?:CERTIFICATE_VERIFY_FAILED|certificate verify failed|TLS certificate)|CA is not trusted",
                 "证书安装或校验失败", "用整合包自带的组件重新启动，按提示安装对应本地证书。若是上游 TLS 校验失败，检查系统时间及当前代理证书。"),
        new Rule(@"(?:\bECONNREFUSED\b|WinError\s+10061|connection refused|目标计算机积极拒绝)",
                 "连接被拒绝", "先确认报错地址对应的服务在运行。学园本地端口可用「重启本地服务」恢复；上游代理端口请检查代理软件。"),
        new Rule(@"(?:\bETIMEDOUT\b|\bECONNRESET\b|WinError\s+10060|timed out|连接超时|upstream CONNECT failed)",
                 "网络连接超时或中断", CheckNetwork),
        new Rule(@"(?:HTTP(?:/\d(?:\.\d)?)?\s+(?:Error\s+|status\s+)?|statusCode[:= ]+)(401|403)\b",
                 "远程服务拒绝访问（HTTP 401／403）", "确认账号登录状态、访问权限和代理连接。学园更新可按说明通过 DMM 刷新登录后再试。"),
        new Rule(@"(?:HTTP(?:/\d(?:\.\d)?)?\s+(?:Error\s+|status\s+)?|statusCode[:= ]+)404\b",
                 "请求的资源不存在（HTTP 404）", "检查资源版本和下载地址，补齐与客户端匹配的资源包。记录缺少的文件或活动名。"),
        new Rule(@"(?:HTTP(?:/\d(?:\.\d)?)?\s+(?:Error\s+|status\s+)?|statusCode[:= ]+)429\b",
                 "远程服务暂时限制请求（HTTP 429）", "暂停重复下载，等待一段时间再试。"),
        new Rule(@"(?:HTTP(?:/\d(?:\.\d)?)?\s+(?:Error\s+|status\s+)?|statusCode[:= ]+)(500|502|503|504)\b",
                 "服务器或转发服务暂时不可用（HTTP 5xx）", CheckNetwork + "若报错来自本地地址，先检查对应的本地服务。"),
        new Rule(@"(?<!\d)-32601(?!\d)", "本地适配包缺少此请求对应的方法（错误码 -32601）",
                 "更新百万现场适配包。若已是对应版本，记录发生在哪首歌、剧情或操作，交给维护者补齐接口。"),
        new Rule(@"\bE203\b|file size is different|Unexpected master transfer envelope|Master transfer is truncated",
                 "客户端所需资源缺失或大小不匹配", CheckAssets),
        new Rule(@"startup-resources-media-missing|startup-resources-conflict|Startup resource.*(?:pin mismatch|required)|catalogue.*(?:digest|missing)|资源.*校验不",
                 "资源档案缺失或校验未通过", CheckAssets),
        new Rule(@"unsupported-client-version|unsupported-loaded-game-abi|Unsupported client adapter receipt|Use Update game adaptation|Client ELF header digest differs",
                 "当前百万现场客户端与适配组件不匹配", CheckAdapter),
        new Rule(@"adb-not-ready|ADB did not connect|ADB command failed|no devices/emulators found|device offline|ADB.*unauthorized|device unauthorized",
                 "模拟器的 ADB 连接未就绪", "先手动启动 MLTD 所在模拟器，在模拟器里开启 ADB，再重新选择该实例和它的回环 ADB 端口。"),
        new Rule(@"root-command-incompatible|network-isolation-unavailable",
                 "所选模拟器与本地连接所需能力不兼容", "选择支持对应连接方式的实例，并检查该实例的 Root 或网络隔离能力。"),
        new Rule(@"(?:\bENOSPC\b|磁盘空间不足|not enough (?:disk )?space)",
                 "磁盘空间不足", "检查启动器目录和游戏目录所在磁盘，腾出空间后重试。"),
        new Rule(@"(?:\bEACCES\b|\bEPERM\b|Access is denied|Permission denied|拒绝访问)",
                 "当前权限不足或文件被占用", "关闭使用该文件的程序，确认安装目录可写。若安全软件拦截，从其记录确认具体文件后按本机设置处理。"),
        new Rule(@"(?:\bENOENT\b|file not found|python not found|找不到游戏主程序|该目录下没有 gakumas\.exe)",
                 "所需文件或组件缺失", CheckPackage),
        new Rule(@"Local service operation cancelled|操作已取消|operation cancelled", "操作已取消", "如仍需执行，确认游戏及相关任务已退出后重新开始。"),
        new Rule(@"Stop incomplete|MLTD stop failed|停止服务未完成", "还有托管服务未确认停止",
                 "先正常退出对应游戏，再点该游戏页面的停止按钮。仍失败时保留启动器的停止日志。")
    };

    static readonly Regex Grpc = new Regex(@"\bgrpc[-_ ]status\s*[:=]\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Dictionary<int, string> GrpcHints = new Dictionary<int, string> {
        { 1, Instruction("请求已取消（gRPC 1）", "返回上一页，等当前任务结束后重试。") },
        { 2, Instruction("远程服务返回未知错误（gRPC 2）", Report) },
        { 3, Instruction("请求参数不适配（gRPC 3）", "回标题重进并确认客户端与整合包版本匹配。" + Report) },
        { 4, Instruction("请求超时（gRPC 4）", CheckNetwork) },
        { 5, Instruction("请求的活动、资源或记录不存在（gRPC 5）", "用「检查并更新内容」同步当前可用数据。若活动已经下线，记录活动名以便核对离线缓存。") },
        { 6, Instruction("数据已经存在（gRPC 6）", "返回上一级刷新状态，确认先前操作是否已完成。") },
        { 7, Instruction("当前身份缺少权限（gRPC 7）", "确认正在使用预期的账号和启动模式。学园官方更新可通过 DMM 刷新登录。") },
        { 8, Instruction("服务达到资源或请求限制（gRPC 8）", "等当前任务结束后再试，并检查本机剩余空间。") },
        { 9, Instruction("操作所需的前置状态不满足（gRPC 9）", "回上一步确认所选活动或当前游玩进度，再试一次。" + Report) },
        {10, Instruction("操作因数据变更被中断（gRPC 10）", "等待更新或保存完成，刷新当前页面后重试。") },
        {11, Instruction("请求数值超出范围（gRPC 11）", "恢复该设置的有效值，或返回上一级刷新后重试。") },
        {12, Instruction("当前服务缺少这个接口（gRPC 12）", "使用与当前客户端匹配的本地服务组件。" + Report) },
        {13, Instruction("服务内部处理失败（gRPC 13）", "重启对应的本地服务后重试。" + Report) },
        {14, Instruction("服务暂时无法连接（gRPC 14）", StopLocal + CheckNetwork) },
        {15, Instruction("服务检测到数据损坏（gRPC 15）", "先备份当前文件，检查下载完整性或用启动器回退未确认的更新。") },
        {16, Instruction("登录会话无效（gRPC 16）", CheckDmm) }
    };

    internal static string Diagnostic(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        foreach (Rule rule in Rules)
            if (rule.Pattern.IsMatch(line)) return rule.Text;
        Match grpc = Grpc.Match(line);
        int code;
        string text;
        if (grpc.Success && int.TryParse(grpc.Groups[1].Value, out code)
            && GrpcHints.TryGetValue(code, out text)) return text;
        return null;
    }

    internal static string FailureDetail(string operation, string detail)
    {
        string reason = Diagnostic(detail);
        if (reason != null) return reason + " [诊断] " + detail.Trim();
        string fix = operation == "scsp" ? CheckScsp
                   : operation == "cgss" ? CheckCgss
                   : operation == "mltd" ? "检查所选模拟器、档案和资源包，再重复该操作。" + Report
                   : Report;
        return string.IsNullOrWhiteSpace(detail) ? Instruction("操作没有返回明确的错误原因", fix)
             : Instruction("操作未完成", fix) + " [诊断] " + detail.Trim();
    }

    internal static string JobName(string operation)
    {
        switch (operation) {
            case "launch": return "启动游戏";
            case "update": return "更新内容";
            case "capture": return "重新获取启动信息";
            case "rollback-master": return "回退未确认的更新";
            case "stop": return "停止本地服务";
            case "restart": return "重启本地服务";
            case "scsp": return "闪耀色彩操作";
            case "cgss": return "灰姑娘女孩启动";
            case "cgss-resources": return "灰姑娘女孩资源下载";
            case "mltd": return "百万现场操作";
            default: return "启动器操作";
        }
    }

    internal static string Finish(string operation, int code, string diagnosis)
    {
        string job = JobName(operation);
        if (code == 0) return job + "完成。";
        if (operation == "scsp" && code == 2) return job + "已取消（退出码 2）。";
        string hint = diagnosis ?? DefaultExit(operation, code);
        string number = unchecked((uint)code) >= 0x80000000 && code != -1
                      ? "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture)
                      : code.ToString(CultureInfo.InvariantCulture);
        return job + "失败（退出码 " + number + "）。" + hint;
    }

    static string DefaultExit(string operation, int code)
    {
        if (operation == "update") {
            switch (code) {
                case 1: return Instruction("更新过程失败", "先看上方具体信息。若未返回主表，请在更新启动的游戏标题页点「TAP TO START」。" + Report);
                case 2: return Instruction("已有更新任务、游戏或服务端口占用", StopLocal);
                case 3: return Instruction("尚未取得可校验的新版主表", "等更新启动的游戏进入大厅后重试。已有缓存可先用本地启动验证。");
                case 4: return Instruction("官方拒绝当前版本或主表协议不匹配", "先在 DMM 更新所选游戏并进入标题，再确认整合包支持这个版本。");
                case 5: return Instruction("官方登录票据缺失或与所选安装不匹配", CheckDmm);
            }
        }
        if (operation == "launch" || operation == "capture") {
            switch (code) {
                case 1: return Instruction("未取得有效的启动信息", "在启动器重新选游戏目录。如果是官方启动，先从 DMM 正常启动一次。");
                case 2: return Instruction("游戏仍在运行", "先正常退出游戏，再启动一次。");
                case 3: return Instruction("找不到有效的游戏目录", "在启动器重新选择包含 gakumas.exe 的目录。");
                case 4: return Instruction("本地证书未就绪", "重新点击「开始游戏」，在 Windows 的本地证书安装窗口确认后再试。");
                case 5: return operation == "capture"
                    ? Instruction("官方登录信息不可用", CheckDmm)
                    : Instruction("本地服务或官方启动准备失败", "先检查当前账号模式。学园本地服务可在「更多」里重启；官方账号请先通过 DMM 登录。");
                case 6: return Instruction("游戏进程无法启动", "检查游戏和启动器文件是否完整、安装路径是否存在，以及安全软件是否拦截。");
                case 7: return Instruction("游戏进程启动后立即退出", "查看 Player.log，检查客户端版本及安装文件。新电脑请先通过 DMM 完成一次正常启动。");
            }
        }
        switch (operation) {
            case "stop": return Instruction("未确认全部托管服务停止", "正常退出对应游戏，再点「停止本地服务」；仍报错时保存该次停止日志。");
            case "rollback-master": return Instruction("未能回退本次主表", "先关闭游戏和更新任务，保留备份文件，再尝试回退。" + Report);
            case "scsp": return Instruction("闪耀色彩操作没有完成", CheckScsp);
            case "cgss": return Instruction("灰姑娘女孩未能启动", CheckCgss);
            case "cgss-resources": return Instruction("资源下载中断", "确认网络和安装盘剩余空间，重新下载以继续未完成的文件。");
            case "mltd": return Instruction("百万现场操作没有完成", "检查所选模拟器、档案和资源包。" + Report);
            default: return Instruction("任务没有返回已知的错误原因", Report);
        }
    }

    internal static string FailedToStart(string operation, Exception error)
    {
        var win32 = error as Win32Exception;
        if (win32 == null) return JobName(operation) + "失败。" + FailureDetail(operation, error.Message);
        int code = win32.NativeErrorCode;
        string hint;
        switch (code) {
            case 2: case 3: case 126:
                hint = Instruction("程序路径或依赖文件缺失", CheckPackage); break;
            case 5: case 32:
                hint = Instruction("权限不足或文件正在使用", "确认目录可写，关闭相关进程后重试。"); break;
            case 193:
                hint = Instruction("文件不是可运行的程序或架构不匹配", "重新解压适用于你的 Windows 版本的整合包。"); break;
            case 740:
                hint = Instruction("这个操作需要管理员授权", "重新执行该操作并在 Windows 弹窗中确认。"); break;
            default: hint = FailureDetail(operation, error.Message); break;
        }
        return JobName(operation) + "失败（Windows 错误码 " + code + "）。" + hint;
    }
}
