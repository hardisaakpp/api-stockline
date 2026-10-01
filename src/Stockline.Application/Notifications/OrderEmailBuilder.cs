using System.Globalization;
using System.Net;
using System.Text;
using Stockline.Domain.Notifications;

namespace Stockline.Application.Notifications;

public sealed record EmailContent(string Subject, string HtmlBody, string TextBody);

public static class OrderEmailBuilder
{
    public static EmailContent Build(EmailTopic topic, OrderEvent orderEvent, string? orderUrl = null)
    {
        var info = EmailTopics.Info(topic);
        var subject = $"{info.Subject} - {orderEvent.OrderCode}";
        var fields = new List<(string Label, string Value)>
        {
            ("Order", orderEvent.OrderCode),
            ("New status", orderEvent.Status),
            ("Triggered by", orderEvent.UserId ?? "system"),
            ("Date (UTC)", orderEvent.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        };

        var html = new StringBuilder()
            .Append("<div style=\"font-family:Arial,sans-serif;max-width:560px\">")
            .Append("<h2 style=\"color:#2c3e50\">").Append(Encode(info.Subject)).Append("</h2>")
            .Append("<p>").Append(Encode(info.Description)).Append("</p>")
            .Append("<table style=\"border-collapse:collapse;width:100%\">");
        foreach (var (label, value) in fields)
        {
            html.Append("<tr><td style=\"padding:6px;border:1px solid #ddd;background:#f5f6f8\"><b>")
                .Append(Encode(label)).Append("</b></td><td style=\"padding:6px;border:1px solid #ddd\">")
                .Append(Encode(value)).Append("</td></tr>");
        }

        html.Append("</table>");
        if (Uri.TryCreate(orderUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            html.Append("<p><a href=\"").Append(Encode(url.AbsoluteUri)).Append("\">View order</a></p>");
        }

        html.Append("</div>");

        var text = new StringBuilder().AppendLine(info.Subject).AppendLine(info.Description).AppendLine();
        foreach (var (label, value) in fields)
        {
            text.Append(label).Append(": ").AppendLine(value);
        }

        return new EmailContent(subject, html.ToString(), text.ToString());
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
