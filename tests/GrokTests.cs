using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Grok's weekly credits arrive as a gRPC-Web protobuf frame. The reader must take the fields it knows, skip the
    /// ones it does not, and answer null instead of a wrong number when the frame is not a credits answer.
    /// </summary>
    internal static class GrokTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Grok's weekly credits frame gives one weekly window with its reset and the shares of each product", FullFrame);
            test("A Grok credits config without a percentage means nothing used, not a missing answer", ZeroUsed);
            test("A Grok frame without a credits config is not a credits answer", NoConfig);
            test("Truncated or malformed Grok frames give no answer, never zero used, and never throw", Truncated);
            test("A Grok trailer-only frame is not a credits answer", TrailerOnly);
            test("Unknown fields in a Grok frame are skipped without changing the result", UnknownFields);
            test("A Grok percentage that is not a number is ignored", NotANumber);
            test("A Grok timestamp outside the calendar gives no date and never throws", HugeTimestamp);
            test("The gRPC status is read from the header, or from the trailer frame only", Status);
            test("Grok protobuf overflows and out-of-range product ids are rejected", Overflow);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        // Protobuf encoders: a tag is (field << 3) | wire type.
        private static byte[] Cat(params byte[][] parts)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var p in parts) ms.Write(p, 0, p.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Varint(ulong v)
        {
            var bytes = new List<byte>();
            while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; }
            bytes.Add((byte)v);
            return bytes.ToArray();
        }

        private static byte[] VarintField(int field, ulong v) { return Cat(Varint((ulong)(field << 3)), Varint(v)); }
        private static byte[] FloatField(int field, float v) { return Cat(Varint((ulong)((field << 3) | 5)), BitConverter.GetBytes(v)); }
        private static byte[] MessageField(int field, byte[] body) { return Cat(Varint((ulong)((field << 3) | 2)), Varint((ulong)body.Length), body); }
        private static byte[] Timestamp(int field, long seconds) { return MessageField(field, Cat(VarintField(1, (ulong)seconds), VarintField(2, 0))); }
        private static byte[] Product(int id, float share) { return MessageField(7, Cat(VarintField(1, (ulong)id), FloatField(2, share))); }

        /// <summary>The gRPC-Web data frame: a zero byte, the big-endian payload length, the payload.</summary>
        private static byte[] Frame(byte[] payload)
        {
            int n = payload.Length;
            return Cat(new byte[] { 0, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }, payload);
        }

        private static byte[] Credits(byte[] config) { return Frame(MessageField(1, config)); }

        private const long Start = 1795000000, End = 1795604800;

        private static void FullFrame()
        {
            var u = GrokProvider.Parse(Credits(Cat(FloatField(1, 42.5f), Timestamp(4, Start), Timestamp(5, End), Product(2, 30f), Product(4, 12.5f))));
            Assert(u != null, "A complete credits frame was not read.");
            Assert(u.Source == "live", "The answer was not marked live.");
            Assert(u.Windows.Count == 1, "There was not exactly one window.");
            var w = u.Windows[0];
            Assert(w.Short == UsageWindow.ShortWeek && w.Label == "Weekly credits", "The window was not the weekly one.");
            Assert(w.Used == 42.5, "The percentage used was " + w.Used);
            Assert(w.StartedAt == Json.FromUnix(Start), "The week's start was not read.");
            Assert(w.ResetsAt == Json.FromUnix(End), "The week's end was not read as the reset.");
            Assert(u.Breakdown.Count == 2, "The product shares were not both kept.");
            Assert(u.Breakdown[0].Key == "Grok Build" && u.Breakdown[0].Value == 30, "The Grok Build share was wrong.");
            Assert(u.Breakdown[1].Key == "Chat" && u.Breakdown[1].Value == 12.5, "The Chat share was wrong.");
        }

        private static void ZeroUsed()
        {
            var u = GrokProvider.Parse(Credits(Cat(Timestamp(4, Start), Timestamp(5, End))));
            Assert(u != null && u.Windows.Count == 1, "A config without a percentage was treated as no answer.");
            Assert(u.Windows[0].Used == 0, "A config without a percentage did not mean zero used.");
            Assert(u.Windows[0].ResetsAt == Json.FromUnix(End), "The reset was lost with the zero percentage.");
            Assert(u.Breakdown.Count == 0, "A breakdown appeared from nothing.");
            // An entirely empty config message is also zero used.
            var empty = GrokProvider.Parse(Credits(new byte[0]));
            Assert(empty != null && empty.Windows[0].Used == 0, "An empty config was not zero used.");
        }

        private static void NoConfig()
        {
            Assert(GrokProvider.Parse(Frame(new byte[0])) == null, "An empty payload was read as credits.");
            Assert(GrokProvider.Parse(Frame(VarintField(3, 7))) == null, "A payload with only another field was read as credits.");
            // Field 1 with the wrong wire type is not the config.
            Assert(GrokProvider.Parse(Frame(FloatField(1, 50f))) == null, "A float in field 1 was read as the config.");
        }

        private static void Truncated()
        {
            var good = Credits(Cat(FloatField(1, 42.5f), Timestamp(5, End)));
            Assert(GrokProvider.Parse(new byte[0]) == null, "An empty input was read as credits.");
            Assert(GrokProvider.Parse(new byte[] { 0, 0, 0 }) == null, "A frame shorter than its header was read as credits.");
            // The header promises more payload than there is.
            var cut = new byte[good.Length - 3];
            Array.Copy(good, cut, cut.Length);
            Assert(GrokProvider.Parse(cut) == null, "A frame shorter than its length was read as credits.");
            Assert(GrokProvider.Parse(new byte[] { 0, 0x7f, 0xff, 0xff, 0xff, 1, 2 }) == null, "A huge length was accepted.");
            Assert(GrokProvider.Parse(new byte[] { 0, 0xff, 0xff, 0xff, 0xff, 1, 2 }) == null, "A negative length was accepted.");

            // A malformed message inside an otherwise consistent frame is no answer, never a zero.
            Assert(GrokProvider.Parse(Credits(new byte[] { (1 << 3) | 5, 0, 0 })) == null, "A float cut short gave an answer.");
            Assert(GrokProvider.Parse(Credits(new byte[] { 0x08 })) == null, "A tag without its field gave an answer.");
            Assert(GrokProvider.Parse(Credits(new byte[] { (9 << 3), 0x80 })) == null, "A varint cut short gave an answer.");
            Assert(GrokProvider.Parse(Credits(Cat(FloatField(1, 42.5f), new byte[] { (5 << 3) | 2, 10, 8 }))) == null, "A timestamp longer than the config gave an answer.");
            Assert(GrokProvider.Parse(Credits(Cat(FloatField(1, 42.5f), MessageField(5, new byte[] { 8, 0x80 })))) == null, "A cut timestamp gave an answer.");
            Assert(GrokProvider.Parse(Credits(Cat(FloatField(1, 42.5f), MessageField(7, new byte[] { (2 << 3) | 5, 0, 0 })))) == null, "A cut product share gave an answer.");
            // The config's own length overruns the payload.
            Assert(GrokProvider.Parse(Frame(new byte[] { (1 << 3) | 2, 20, (1 << 3) | 5, 0, 0, 0x28, 0x42 })) == null, "A config cut mid-way gave an answer.");
            Assert(GrokProvider.Parse(Frame(Cat(MessageField(1, FloatField(1, 42.5f)), new byte[] { (9 << 3) | 5, 0 }))) == null, "A cut field after the config gave an answer.");
        }

        private static void TrailerOnly()
        {
            var text = Encoding.ASCII.GetBytes("grpc-status:16\r\ngrpc-message:unauthenticated\r\n");
            var trailer = Cat(new byte[] { 0x80, 0, 0, 0, (byte)text.Length }, text);
            Assert(GrokProvider.Parse(trailer) == null, "A trailer frame was read as credits.");
        }

        private static void UnknownFields()
        {
            var known = Cat(FloatField(1, 42.5f), Timestamp(4, Start), Timestamp(5, End), Product(2, 30f));
            var noisy = Cat(
                VarintField(9, 123456),
                FloatField(1, 42.5f),
                MessageField(12, new byte[] { 1, 2, 3, 4, 5 }),
                Timestamp(4, Start),
                FloatField(10, 1.5f),
                Timestamp(5, End),
                Product(2, 30f),
                Product(99, 10f),   // A product this version does not name.
                VarintField(300, 1));
            var payload = Cat(VarintField(2, 5), MessageField(1, noisy), MessageField(3, new byte[] { 9, 9 }));
            var a = GrokProvider.Parse(Credits(known));
            var b = GrokProvider.Parse(Frame(payload));
            Assert(a != null && b != null, "A frame with unknown fields was not read.");
            Assert(b.Windows.Count == 1 && b.Windows[0].Used == a.Windows[0].Used, "Unknown fields changed the percentage.");
            Assert(b.Windows[0].StartedAt == a.Windows[0].StartedAt && b.Windows[0].ResetsAt == a.Windows[0].ResetsAt, "Unknown fields changed the dates.");
            Assert(b.Breakdown.Count == 1 && b.Breakdown[0].Key == "Grok Build" && b.Breakdown[0].Value == 30, "Unknown fields or products changed the breakdown.");
        }

        private static void NotANumber()
        {
            var nan = GrokProvider.Parse(Credits(Cat(FloatField(1, float.NaN), Timestamp(5, End))));
            Assert(nan != null && nan.Windows[0].Used == 0, "A NaN percentage reached the bar.");
            var inf = GrokProvider.Parse(Credits(FloatField(1, float.PositiveInfinity)));
            Assert(inf != null && inf.Windows[0].Used == 0, "An infinite percentage reached the bar.");
            var over = GrokProvider.Parse(Credits(FloatField(1, 250f)));
            Assert(over != null && over.Windows[0].Used == 100, "A percentage above 100 was not capped.");
        }

        private static void HugeTimestamp()
        {
            foreach (long seconds in new[] { 1795000000000L, 253402300800L, long.MaxValue })
            {
                var u = GrokProvider.Parse(Credits(Cat(FloatField(1, 10f), Timestamp(4, seconds), Timestamp(5, seconds))));
                Assert(u != null && u.Windows[0].Used == 10, "A huge timestamp spoiled the answer: " + seconds);
                Assert(u.Windows[0].ResetsAt == null && u.Windows[0].StartedAt == null, "A huge timestamp became a date: " + seconds);
            }
        }

        private static byte[] Trailer(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            return Cat(new byte[] { 0x80, 0, 0, (byte)(bytes.Length >> 8), (byte)bytes.Length }, bytes);
        }

        private static void Status()
        {
            var data = Credits(Cat(FloatField(1, 5f)));
            Assert(GrokProvider.GrpcStatus("16", new byte[0]) == "16", "The header status was not used.");
            Assert(GrokProvider.GrpcStatus(" 7 ", Trailer("grpc-status:0\r\n")) == "7", "The header did not win over the trailer.");
            Assert(GrokProvider.GrpcStatus(null, data) == null, "A data frame alone gave a status.");
            Assert(GrokProvider.GrpcStatus(null, null) == null, "No body gave a status.");
            foreach (var code in new[] { "0", "16", "7", "14" })
            {
                Assert(GrokProvider.GrpcStatus(null, Trailer("grpc-status:" + code + "\r\ngrpc-message:x\r\n")) == code, "Trailer alone lost status " + code);
                Assert(GrokProvider.GrpcStatus("", Cat(data, Trailer("grpc-message:x\r\ngrpc-status: " + code + "\r\n"))) == code, "Trailer after data lost status " + code);
            }
            Assert(GrokProvider.GrpcStatus(null, Trailer("Grpc-Status: 16\r\n")) == "16", "A mixed-case key was not read.");
            // The text inside a data frame is not a trailer.
            var fake = Frame(Encoding.ASCII.GetBytes("grpc-status:16\r\n"));
            Assert(GrokProvider.GrpcStatus(null, fake) == null, "A status inside a data frame was read.");
            // A frame that overruns the body is malformed.
            Assert(GrokProvider.GrpcStatus(null, new byte[] { 0x80, 0, 0, 0, 50, 1, 2 }) == null, "A cut trailer gave a status.");
        }

        private static void Overflow()
        {
            // A varint whose tenth byte carries more than the top bit does not fit in 64 bits.
            var bad = new byte[] { 0x08, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x02 };
            Assert(GrokProvider.Parse(Credits(MessageField(7, bad))) == null, "A varint overflow gave an answer.");
            Assert(GrokProvider.Parse(Credits(Cat(FloatField(1, 5f), MessageField(7, bad)))) == null, "A varint overflow in a share gave an answer.");
            var ok = new byte[] { 0x08, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01 };
            Assert(GrokProvider.Parse(Credits(Cat(FloatField(1, 5f), MessageField(7, ok)))) != null, "A 64-bit varint was rejected.");
            // 2^32 + 2 would truncate to product 2, Grok Build.
            var big = GrokProvider.Parse(Credits(Cat(FloatField(1, 5f), MessageField(7, Cat(VarintField(1, (1UL << 32) + 2), FloatField(2, 30f))))));
            Assert(big != null && big.Breakdown.Count == 0, "A product id beyond int was truncated to a product.");
            var inf = GrokProvider.Parse(Credits(Cat(FloatField(1, 5f), Product(2, float.PositiveInfinity), Product(4, float.NaN), Product(7, -3f))));
            Assert(inf != null && inf.Breakdown.Count == 0, "A share that is not a positive number was kept.");
            Assert(GrokProvider.Parse(null) == null, "A null frame gave an answer.");
        }
    }
}
