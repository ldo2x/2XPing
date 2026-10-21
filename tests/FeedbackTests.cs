using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PingBoost
{
    // «تواصل معنا»: the message payload, its caps, the rate limit and the outbox. A fake sender stands in for the network,
    // and the data folder is a temporary one.
    static partial class Tests
    {
        // Runs the test with a fake sender and an empty rate limit, and puts the real sender back after.
        static void WithFakeSender(Action test)
        {
            var real = Feedback.Sender;
            Feedback.ResetRate();
            try { test(); }
            finally { Feedback.Sender = real; Feedback.ResetRate(); }
        }

        static void FeedbackPayloadCarriesTheMessage()
        {
            WithTelemetryData(() =>
            {
                var m = Feedback.Make("idea", "  Lock by itself when the game starts, please  ", "fahad#1234", "```\n2X Ping 0.12.0 Beta\n--- log ---\n```");
                var on = Telemetry.FeedbackPayload(m, true);
                var data = (Dictionary<string, object>)on["data"];
                Check((string)on["kind"] == "feedback", "kind");
                Check((string)on["detail"] == "Lock by itself when the game starts, please", "the message, trimmed: " + on["detail"]);
                Check((string)data["type"] == "idea" && (string)data["contact"] == "fahad#1234", "type and contact");
                Check(((string)data["diagnostics"]).Contains("2X Ping 0.12.0 Beta"), "diagnostics");
                Check((string)data["lang"] == m.Lang && (string)data["id"] == m.Id && data.ContainsKey("written"), "lang, id, time written");
                Check((string)on["nickname"] == "Tester Abu Fahad", "sharing on: the name as usual");
                Check(Feedback.Make("nonsense", "hello there", "", "").Type == "problem", "an unknown type is a problem");

                // sharing off: still sent (the player pressed Send), with the anonymous identity and the contact they typed
                var off = Telemetry.FeedbackPayload(m, false);
                var offData = (Dictionary<string, object>)off["data"];
                Check((string)off["nickname"] == Telemetry.AnonymousName, "anonymous name");
                foreach (var k in new[] { "device_name", "country", "city", "isp", "asn" })
                    Check((string)off[k] == "", k + " empty");
                Check(!off.ContainsKey("info"), "no launch details");
                Check((string)off["detail"] == (string)on["detail"] && (string)offData["contact"] == "fahad#1234" &&
                      (string)offData["type"] == "idea" && offData.ContainsKey("diagnostics"), "message, type, contact and diagnostics kept");
                string json = Json(off);
                foreach (var s in new[] { "Tester Abu Fahad", "Jeddah", "Saudi Telecom", "AS25019" })
                    Check(json.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0, "not sent: " + s);
                if (Environment.MachineName.Length >= 3)
                    Check(json.IndexOf(Environment.MachineName, StringComparison.OrdinalIgnoreCase) < 0, "PC name not sent");
            });
        }

        static void FeedbackIsCappedAt2000()
        {
            string longText = new string('x', 2500);
            Check(Feedback.Cap(longText).Length == Feedback.MaxLength && Feedback.MaxLength == 2000, "2000 characters at most");
            Check(Feedback.Cap(new string('y', 2000)).Length == 2000, "exactly 2000 stays");
            Check(Feedback.Make("problem", longText, new string('c', 300), "").Text.Length == 2000, "the message made is capped");
            Check(Feedback.CapContact(new string('c', 300)).Length == Feedback.MaxContact, "the contact is capped too");
            Check(!Feedback.LongEnough("hi") && !Feedback.LongEnough("   abc   ") && Feedback.LongEnough("hello"), "at least 5 characters to send");
            WithTelemetryData(() =>
            {
                var m = Feedback.Make("problem", "short", "", "");
                m.Text = longText; // even if something longer got in
                Check(((string)Telemetry.FeedbackPayload(m, true)["detail"]).Length == 2000, "the payload caps it");
            });
        }

        static void FeedbackRateLimit()
        {
            var times = new List<DateTime>();
            var t0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < Feedback.RateMax; i++) Check(Feedback.TakeSlot(times, t0.AddSeconds(i * 10)), "message " + (i + 1) + " goes");
            Check(!Feedback.TakeSlot(times, t0.AddMinutes(2)), "the 6th within 10 minutes waits");
            Check(Feedback.TakeSlot(times, t0.AddMinutes(10).AddSeconds(1)), "after 10 minutes one more goes");

            WithTelemetryData(() => WithFakeSender(() =>
            {
                int sent = 0;
                Feedback.Sender = m => sent++;
                var results = Enumerable.Range(0, 7).Select(i => Feedback.Send(Feedback.Make("idea", "message number " + i, "", ""))).ToList();
                Check(results.Take(5).All(r => r == Feedback.Result.Sent) && sent == 5, "five go");
                Check(results.Skip(5).All(r => r == Feedback.Result.TooMany), "then too many");
                Check(Feedback.Queued().Count == 0, "refused ones aren't queued (the form keeps the text)");
            }));
        }

        static void FeedbackOutboxKeepsAndResends()
        {
            WithTelemetryData(() => WithFakeSender(() =>
            {
                var got = new List<Feedback.Message>();
                Feedback.Sender = m => { throw new System.Net.WebException("offline"); };
                var a = Feedback.Make("problem", "ping jumps to 200 mid match", "a@b.c", "diag A");
                var b = Feedback.Make("question", "does it work with Valorant?", "", "diag B");
                Check(Feedback.Send(a) == Feedback.Result.Failed && Feedback.Send(b) == Feedback.Result.Failed, "offline: failed");
                Check(File.Exists(Path.Combine(Storage.Dir, "outbox.txt")), "saved in the data folder");
                var q = Feedback.Queued();
                Check(q.Count == 2 && q[0].Id == a.Id && q[1].Text == "does it work with Valorant?" && q[0].Contact == "a@b.c" && q[1].Diagnostics == "diag B",
                      "both kept, in order, as written");

                // a retry of the same message replaces its saved copy instead of adding one
                a.Text = "ping jumps to 200 mid match, every evening";
                Feedback.Send(a);
                q = Feedback.Queued();
                Check(q.Count == 2 && q.Single(x => x.Id == a.Id).Text.EndsWith("every evening"), "one copy, the newest text");

                // still offline on the next start: nothing lost
                Check(Feedback.ResendQueued() == 0 && Feedback.Queued().Count == 2, "offline start keeps them");

                // online on the next start: both sent, the outbox empties
                Feedback.Sender = m => got.Add(m);
                Check(Feedback.ResendQueued() == 2, "both resent");
                Check(got.Select(x => x.Id).SequenceEqual(new[] { b.Id, a.Id }) || got.Select(x => x.Id).SequenceEqual(new[] { a.Id, b.Id }), "the two messages");
                Check(Feedback.Queued().Count == 0 && !File.Exists(Path.Combine(Storage.Dir, "outbox.txt")), "outbox empty");

                // a send that works removes its earlier saved copy
                Feedback.Sender = m => { throw new System.Net.WebException("offline"); };
                var c = Feedback.Make("idea", "dark and light themes", "", "");
                Feedback.Send(c);
                Feedback.Sender = m => got.Add(m);
                Check(Feedback.Send(c) == Feedback.Result.Sent && Feedback.Queued().Count == 0, "retry succeeded: no copy left");
            }));
        }

        static void DevPanelMessageDetails()
        {
            string s = DevPanel.MessageDetails(new DateTime(2026, 10, 21, 18, 5, 0), "Idea · فكرة", "Fahad", "", "0.13.0 Beta", "Deadlock",
                                               "line one\nline two", "```\n2X Ping\n```");
            Check(s.Contains("Contact: -") && s.Contains("line one" + Environment.NewLine + "line two") && s.Contains("2X Ping"), "full message with diagnostics");
        }

        static void RunFeedbackTests()
        {
            Run("FeedbackPayloadCarriesTheMessage", FeedbackPayloadCarriesTheMessage);
            Run("FeedbackIsCappedAt2000", FeedbackIsCappedAt2000);
            Run("FeedbackRateLimit", FeedbackRateLimit);
            Run("FeedbackOutboxKeepsAndResends", FeedbackOutboxKeepsAndResends);
            Run("DevPanelMessageDetails", DevPanelMessageDetails);
        }
    }
}
