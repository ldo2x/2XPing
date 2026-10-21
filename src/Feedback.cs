using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // Messages written in «تواصل معنا» (ContactForm). Each is sent as one 'feedback' event through checkin()
    // (Telemetry.FeedbackPayload), with the diagnostics attached. One that can't be sent waits in outbox.txt in the data
    // folder and is sent again on the next start, so a message is never lost.
    static class Feedback
    {
        public const int MinLength = 5, MaxLength = 2000, MaxContact = 100;
        // at most RateMax messages in RateWindow from one PC (a stuck key or a loop can't flood the developer's list)
        public const int RateMax = 5;
        public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);
        public static readonly string[] Types = { "problem", "idea", "question" };

        public class Message
        {
            public string Id, Type, Text, Contact, Diagnostics, Lang;
            public DateTime Written;
        }

        // How a message reaches the developer: throws when it didn't. Tests put a fake here.
        internal static Action<Message> Sender = m => Telemetry.Checkin(Telemetry.FeedbackPayload(m, Prefs.ShareStats), 15000);

        static string OutboxFile { get { return Path.Combine(Storage.Dir, "outbox.txt"); } }
        static readonly object gate = new object();
        static readonly List<DateTime> sentAt = new List<DateTime>();

        public static string Cap(string text)
        {
            text = (text ?? "").Trim();
            return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
        }

        public static string CapContact(string contact)
        {
            contact = (contact ?? "").Trim();
            return contact.Length > MaxContact ? contact.Substring(0, MaxContact) : contact;
        }

        public static bool LongEnough(string text) { return (text ?? "").Trim().Length >= MinLength; }

        public static Message Make(string type, string text, string contact, string diagnostics)
        {
            return new Message
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12), Type = Types.Contains(type) ? type : Types[0],
                Text = Cap(text), Contact = CapContact(contact), Diagnostics = diagnostics, Lang = Lang.Ar ? "ar" : "en",
                Written = DateTime.UtcNow
            };
        }

        // true when one more message may go now (and counts it); the times are the sends in this session
        public static bool TakeSlot(List<DateTime> times, DateTime now)
        {
            lock (times)
            {
                times.RemoveAll(t => now - t >= RateWindow);
                if (times.Count >= RateMax) return false;
                times.Add(now);
                return true;
            }
        }

        internal static void ResetRate() { lock (sentAt) sentAt.Clear(); }

        public enum Result { Sent, Failed, TooMany }

        // Sends a message the player just wrote (blocking: call it off the UI thread). Failed: it waits in the outbox
        // and goes with the next start, or the next Send of the same message (the player's retry).
        public static Result Send(Message m)
        {
            if (!TakeSlot(sentAt, DateTime.UtcNow)) return Result.TooMany;
            try
            {
                Sender(m);
                Remove(m.Id);
                return Result.Sent;
            }
            catch
            {
                Save(m);
                return Result.Failed;
            }
        }

        // The messages that couldn't be sent before, oldest first.
        public static List<Message> Queued()
        {
            lock (gate)
            {
                var list = new List<Message>();
                try
                {
                    if (!File.Exists(OutboxFile)) return list;
                    var js = new JavaScriptSerializer();
                    foreach (var line in File.ReadAllLines(OutboxFile))
                        try { var m = js.Deserialize<Message>(line); if (m != null && !string.IsNullOrEmpty(m.Id)) list.Add(m); } catch { }
                }
                catch { }
                return list;
            }
        }

        static void Write(List<Message> list)
        {
            var js = new JavaScriptSerializer();
            if (list.Count == 0) { try { File.Delete(OutboxFile); } catch { } return; }
            AtomicFile.WriteAllLines(OutboxFile, list.Select(m => js.Serialize(m)));
        }

        static void Save(Message m)
        {
            lock (gate)
                try
                {
                    var list = Queued().Where(x => x.Id != m.Id).ToList();
                    list.Add(m);
                    Write(list);
                }
                catch { }
        }

        static void Remove(string id)
        {
            lock (gate)
                try
                {
                    var list = Queued();
                    if (list.RemoveAll(x => x.Id == id) > 0) Write(list);
                }
                catch { }
        }

        // On start: sends what is waiting in the outbox; what still fails stays for the next start. Returns how many went.
        public static int ResendQueued()
        {
            int sent = 0;
            foreach (var m in Queued())
            {
                try { Sender(m); }
                catch { break; } // offline: the rest waits too
                Remove(m.Id);
                sent++;
            }
            return sent;
        }

        // In the background, never throwing (called from the main window once statistics may be sent).
        public static void ResendQueuedLater()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { ResendQueued(); } catch { } });
        }
    }
}
