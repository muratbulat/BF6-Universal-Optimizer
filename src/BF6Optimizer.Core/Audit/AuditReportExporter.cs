using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Diagnostics;

namespace BF6Optimizer.Core.Audit;

public sealed record ReportComparison(int? PreviousScore, int? CurrentScore, IReadOnlyList<(string Id, AuditStatus Before, AuditStatus After)> Changes);

public static partial class AuditReportExporter
{
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex Ipv4();
    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){3,7}[0-9a-fA-F]{1,4}\b")] private static partial Regex Ipv6();

    public static string RedactNetwork(string text) => Ipv6().Replace(Ipv4().Replace(text, "<ip>"), "<ip>");

    public static ReportComparison Compare(AuditResult? previous, AuditResult current)
    {
        var before = previous?.Checks.ToDictionary(c => c.Id, c => c.Status) ?? [];
        var changes = current.Checks
            .Where(c => before.TryGetValue(c.Id, out var s) && s != c.Status)
            .Select(c => (c.Id, before[c.Id], c.Status))
            .ToList();
        return new ReportComparison(previous?.Readiness.Score, current.Readiness.Score, changes);
    }

    /// <summary>Report JSON leaves out process names, startup commands, DNS and gateways by construction.</summary>
    public static string ToJson(AuditResult r, ReportComparison cmp, Redactor redactor)
    {
        var s = r.Snapshot;
        var data = new
        {
            r.SchemaVersion,
            s.Timestamp,
            AppVersion = AppInfo.Version,
            Scan = s.Full ? "Full" : "Quick",
            Os = s.Windows is { } w ? $"{w.Caption} ({w.Build})" : null,
            Cpu = s.Cpu?.Name,
            Gpus = s.Hardware?.Gpus.Select(g => new { g.Name, g.Kind, g.VramGb, g.VramSource }),
            r.Readiness,
            Comparison = new { cmp.PreviousScore, cmp.CurrentScore, Changes = cmp.Changes.Select(c => new { c.Id, c.Before, c.After }) },
            Checks = r.Checks.Select(c => new
            {
                c.Id,
                c.Category,
                c.Status,
                c.Priority,
                c.Evidence,
                c.Confidence,
                c.Method,
                Detected = c.Detected.ToString(),
                c.NeedsAdmin,
                c.AutoFix,
                c.RestartRequired,
                c.Reversible,
            }),
        };
        return RedactNetwork(redactor.Apply(Json.Serialize(data)));
    }

    public static string ToText(AuditResult r, ReportComparison cmp, Func<Msg, string> tr, Redactor redactor)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppInfo.ProductName} {AppInfo.Version}  {r.Snapshot.Timestamp:yyyy-MM-dd HH:mm}");
        sb.AppendLine(tr(new Msg("report.score_line", r.Readiness.Score?.ToString() ?? "-", r.Readiness.CoveragePercent.ToString())));
        sb.AppendLine(tr(new Msg("report.score_note")));
        if (cmp.PreviousScore is not null) sb.AppendLine(tr(new Msg("report.previous", cmp.PreviousScore.ToString()!, cmp.Changes.Count.ToString())));
        foreach (var group in r.Checks.GroupBy(c => c.Category))
        {
            sb.AppendLine().AppendLine("== " + tr(new Msg("cat." + group.Key)));
            foreach (var c in group)
                sb.AppendLine($"[{tr(new Msg("status." + c.Status))}] {tr(new Msg(c.TitleKey))}: {tr(c.Detected)}  ({tr(new Msg("evidence." + c.Evidence))}; {tr(new Msg("confidence." + c.Confidence))}; {c.Method})");
        }
        return RedactNetwork(redactor.Apply(sb.ToString()));
    }

    public static string ToHtml(AuditResult r, ReportComparison cmp, Func<Msg, string> tr, Redactor redactor)
    {
        static string E(string t) => WebUtility.HtmlEncode(t);
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>Gaming Audit</title><style>
            body{background:#0f1520;color:#e6ebf2;font-family:'Segoe UI',sans-serif;margin:0;padding:32px;line-height:1.45}
            main{max-width:1000px;margin:auto}h1{font-size:26px;margin:0 0 4px}h2{font-size:17px;margin:32px 0 10px;color:#7cb7ff}
            .hero{display:flex;gap:16px;margin:20px 0}.tile{background:#182130;border-radius:12px;padding:16px 22px}
            .tile b{display:block;font-size:30px}.muted{color:#93a0b2;font-size:13px}
            table{width:100%;border-collapse:collapse;background:#182130;border-radius:12px;overflow:hidden}
            td,th{padding:9px 12px;text-align:left;border-bottom:1px solid #263246;font-size:14px;vertical-align:top}
            th{color:#93a0b2;font-weight:600}.Optimal,.Acceptable{color:#5fd38d}.OptimizationAvailable,.ManualVerification{color:#f2c14e}
            .Warning{color:#ff9f5a}.Critical{color:#ff6b6b}.Unknown,.Unsupported{color:#93a0b2}
            </style></head><body><main>
            """);
        sb.Append($"<h1>{E(AppInfo.ProductName)}</h1><div class=muted>{AppInfo.Version} · {r.Snapshot.Timestamp:yyyy-MM-dd HH:mm} · {E(r.Snapshot.Windows?.Caption ?? "")}</div>");
        sb.Append($"<div class=hero><div class=tile><b>{r.Readiness.Score?.ToString() ?? "-"}/100</b>{E(tr(new Msg("ui.health_score")))}</div><div class=tile><b>{r.Readiness.CoveragePercent} %</b>{E(tr(new Msg("ui.coverage")))}</div>");
        if (cmp.PreviousScore is not null) sb.Append($"<div class=tile><b>{cmp.PreviousScore}/100</b>{E(tr(new Msg("ui.previous_score")))}</div>");
        sb.Append($"</div><p class=muted>{E(tr(new Msg("report.score_note")))}</p>");

        foreach (var group in r.Checks.GroupBy(c => c.Category))
        {
            sb.Append($"<h2>{E(tr(new Msg("cat." + group.Key)))}</h2><table><tr><th>{E(tr(new Msg("ui.check")))}</th><th>{E(tr(new Msg("ui.detected")))}</th><th>{E(tr(new Msg("ui.status")))}</th><th>{E(tr(new Msg("ui.source")))}</th></tr>");
            foreach (var c in group)
                sb.Append($"<tr><td>{E(tr(new Msg(c.TitleKey)))}</td><td>{E(tr(c.Detected))}</td><td class={c.Status}>{E(tr(new Msg("status." + c.Status)))}</td><td class=muted>{E(tr(new Msg("evidence." + c.Evidence)))}, {E(tr(new Msg("confidence." + c.Confidence)))}<br>{E(c.Method)}</td></tr>");
            sb.Append("</table>");
        }

        var recs = r.Recommendations.ToList();
        if (recs.Count > 0)
        {
            sb.Append($"<h2>{E(tr(new Msg("ui.recommendations")))}</h2><table>");
            foreach (var c in recs)
                sb.Append($"<tr><td class={c.Status}>{E(tr(new Msg("priority." + c.Priority)))}</td><td><b>{E(tr(new Msg(c.TitleKey)))}</b><br>{E(tr(new Msg(c.AdviceKey)))}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</main></body></html>");
        return RedactNetwork(redactor.Apply(sb.ToString()));
    }
}
