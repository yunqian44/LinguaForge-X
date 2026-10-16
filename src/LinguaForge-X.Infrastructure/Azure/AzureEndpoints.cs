using System.Text.RegularExpressions;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Infrastructure.Azure;

/// <summary>Builds only known API paths; credentials never belong in URLs.</summary>
public static partial class AzureEndpoints
{
    public const string SpeechApiVersion = "2025-10-15";

    public static Uri OpenAI(ServiceSettings settings, string operation)
    {
        if (operation is not ("audio/transcriptions" or "audio/speech" or "chat/completions"))
            throw new ArgumentException("不支持的 Azure OpenAI 操作。", nameof(operation));
        var endpoint = ReadEndpoint(settings.Endpoint);
        var path = endpoint.AbsolutePath.TrimEnd('/');
        var version = QueryVersion(endpoint) ?? (string.IsNullOrWhiteSpace(settings.ApiVersion) ? "2024-10-21" : settings.ApiVersion.Trim());
        if (path.Equals("/openai/v1", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/openai/v1/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateSuffix(path["/openai/v1".Length..], operation);
            RequireDeployment(settings.Deployment);
            return new UriBuilder(endpoint) { Path = $"/openai/v1/{operation}", Query = "" }.Uri;
        }
        var match = DeploymentPath().Match(path);
        var deployment = settings.Deployment.Trim();
        if (match.Success)
        {
            var fromUrl = Uri.UnescapeDataString(match.Groups[1].Value);
            if (deployment.Length > 0 && !deployment.Equals(fromUrl, StringComparison.Ordinal))
                throw new InvalidOperationException("链接中的部署名称与配置中的部署名称不一致。请修正后重试。");
            deployment = fromUrl;
            ValidateSuffix(match.Groups[2].Value, operation);
        }
        else if (path.Length > 0 && !path.Equals("/openai", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请填写 Azure OpenAI 资源根链接、部署完整接口链接或 /openai/v1 链接。");
        RequireDeployment(deployment);
        ValidateVersion(version);
        return new UriBuilder(endpoint)
        {
            Path = $"/openai/deployments/{Uri.EscapeDataString(deployment)}/{operation}",
            Query = $"api-version={Uri.EscapeDataString(version)}"
        }.Uri;
    }

    public static string Deployment(ServiceSettings settings)
    {
        var match = DeploymentPath().Match(ReadEndpoint(settings.Endpoint).AbsolutePath.TrimEnd('/'));
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : settings.Deployment.Trim();
    }

    public static Uri Speech(ServiceSettings settings, bool synthesis, bool voices = false)
    {
        Uri endpoint;
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            if (!RegionName().IsMatch(settings.Region.Trim()))
                throw new InvalidOperationException("请填写 Speech 服务链接或有效的区域，例如 eastus。");
            endpoint = new Uri(synthesis
                ? $"https://{settings.Region.Trim()}.tts.speech.microsoft.com"
                : $"https://{settings.Region.Trim()}.api.cognitive.microsoft.com");
        }
        else endpoint = ReadEndpoint(settings.Endpoint);

        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (!synthesis)
        {
            if (path.Length != 0 && !path.Equals("/speechtotext/transcriptions:transcribe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Speech 转写链接应为资源根链接或 /speechtotext/transcriptions:transcribe 完整链接。");
            var version = QueryVersion(endpoint) ?? SpeechApiVersion;
            ValidateVersion(version);
            return new UriBuilder(endpoint)
            {
                Path = "/speechtotext/transcriptions:transcribe",
                Query = $"api-version={Uri.EscapeDataString(version)}"
            }.Uri;
        }

        if (QueryVersion(endpoint) is not null)
            throw new InvalidOperationException("Speech 配音链接不需要 api-version，请使用配音资源链接。");
        var resourceHost = endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.EndsWith(".cognitiveservices.azure.us", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.EndsWith(".cognitiveservices.azure.cn", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
        var prefix = resourceHost ? "/tts" : "";
        if (path is "/tts/cognitiveservices/v1" or "/tts/cognitiveservices/voices/list") prefix = "/tts";
        else if (path is "/cognitiveservices/v1" or "/cognitiveservices/voices/list") prefix = "";
        else if (path.Length != 0)
            throw new InvalidOperationException("Speech 配音链接应为资源根链接、区域 TTS 链接或完整配音接口链接。");
        return new UriBuilder(endpoint)
        {
            Path = prefix + (voices ? "/cognitiveservices/voices/list" : "/cognitiveservices/v1"), Query = ""
        }.Uri;
    }

    private static Uri ReadEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            throw new InvalidOperationException("服务链接必须是 HTTPS 地址，不能包含用户名、密码或片段标记。");
        _ = QueryVersion(endpoint);
        return endpoint;
    }

    private static string? QueryVersion(Uri endpoint)
    {
        string? value = null;
        foreach (var part in endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (!Uri.UnescapeDataString(pair[0]).Equals("api-version", StringComparison.OrdinalIgnoreCase) || pair.Length != 2 || value is not null)
                throw new InvalidOperationException("服务链接仅支持 api-version 查询参数；请将 Access Key 填入独立密钥字段。");
            value = Uri.UnescapeDataString(pair[1]);
            ValidateVersion(value);
        }
        return value;
    }

    private static void ValidateSuffix(string suffix, string operation)
    {
        if (suffix.Length > 0 && !suffix.Equals("/" + operation, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("完整接口链接的操作与当前服务不一致，请使用对应接口或资源根链接。");
    }

    private static void RequireDeployment(string deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment) || deployment.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
            throw new InvalidOperationException("请填写有效的 Azure OpenAI 模型部署名称（不是模型名称）。");
    }

    private static void ValidateVersion(string version)
    {
        if (!ApiVersionPattern().IsMatch(version))
            throw new InvalidOperationException("API 版本格式应为 YYYY-MM-DD 或 YYYY-MM-DD-preview。");
    }

    [GeneratedRegex("^/openai/deployments/([^/]+)(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DeploymentPath();
    [GeneratedRegex("^[a-z][a-z0-9]{1,40}$")]
    private static partial Regex RegionName();
    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}(-preview)?$")]
    private static partial Regex ApiVersionPattern();
}
