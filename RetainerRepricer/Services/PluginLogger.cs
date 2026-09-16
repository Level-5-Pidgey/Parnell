using Dalamud.Plugin.Services;
using System;
using System.Globalization;
using System.Text;

namespace RetainerRepricer.Services;

internal sealed class PluginLogger
{
    private readonly IPluginLog _dalamudLog;
    private readonly PluginLogBuffer _buffer;

    public PluginLogger(IPluginLog dalamudLog, PluginLogBuffer buffer)
    {
        _dalamudLog = dalamudLog;
        _buffer = buffer;
    }

    public void Verbose(string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Verbose(messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Verbose, null, messageTemplate, args);
    }

    public void Verbose(Exception? exception, string messageTemplate, params object?[] args)
    {
        var renderedMessage = RenderMessage(messageTemplate, args);
        if (exception != null)
            renderedMessage = $"{renderedMessage} | {exception.GetType().Name}: {exception.Message}";

        _dalamudLog.Verbose(renderedMessage);
        AddEntry(PluginLogLevel.Verbose, exception, messageTemplate, args);
    }

    public void Debug(string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Debug(messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Debug, null, messageTemplate, args);
    }

    public void Information(string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Information(messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Information, null, messageTemplate, args);
    }

    public void Warning(string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Warning(messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Warning, null, messageTemplate, args);
    }

    public void Warning(Exception? exception, string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Warning(exception, messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Warning, exception, messageTemplate, args);
    }

    public void Error(string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Error(messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Error, null, messageTemplate, args);
    }

    public void Error(Exception? exception, string messageTemplate, params object?[] args)
    {
        var normalizedArgs = NormalizeArgs(args);
        _dalamudLog.Error(exception, messageTemplate, normalizedArgs);
        AddEntry(PluginLogLevel.Error, exception, messageTemplate, args);
    }

    private void AddEntry(PluginLogLevel level, Exception? exception, string messageTemplate, params object?[] args)
    {
        var message = RenderMessage(messageTemplate, args);
        if (exception != null)
            message = $"{message} | {exception.GetType().Name}: {exception.Message}";

        _buffer.Add(new PluginLogEntry(DateTime.UtcNow, level, message));
    }

    private static string RenderMessage(string messageTemplate, params object?[] args)
    {
        if (args.Length == 0)
            return messageTemplate;

        try
        {
            var format = ConvertMessageTemplateToFormatString(messageTemplate);
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch
        {
            return $"{messageTemplate} | args=[{string.Join(", ", args)}]";
        }
    }

    private static string ConvertMessageTemplateToFormatString(string template)
    {
        var builder = new StringBuilder(template.Length + 16);
        var argumentIndex = 0;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '{')
            {
                if (c == '}')
                    builder.Append("}}");
                else
                    builder.Append(c);
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                builder.Append("{{");
                i++;
                continue;
            }

            var closeIndex = template.IndexOf('}', i + 1);
            if (closeIndex < 0)
            {
                builder.Append("{{");
                continue;
            }

            var token = template.Substring(i + 1, closeIndex - i - 1);
            var formatIndex = token.IndexOf(':');
            var alignmentIndex = token.IndexOf(',');
            var splitIndex = formatIndex >= 0 && alignmentIndex >= 0
                ? Math.Min(formatIndex, alignmentIndex)
                : Math.Max(formatIndex, alignmentIndex);

            builder.Append('{');
            builder.Append(argumentIndex.ToString(CultureInfo.InvariantCulture));
            if (splitIndex >= 0)
                builder.Append(token.Substring(splitIndex));
            builder.Append('}');

            argumentIndex++;
            i = closeIndex;
        }

        return builder.ToString();
    }

    private static object[] NormalizeArgs(object?[] args)
    {
        if (args.Length == 0)
            return Array.Empty<object>();

        var normalized = new object[args.Length];
        for (var i = 0; i < args.Length; i++)
            normalized[i] = args[i] ?? string.Empty;

        return normalized;
    }
}
