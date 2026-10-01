using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PassengerCoachAccess
{
    internal sealed class PassengerSeatDefinition
    {
        internal int Index;
        internal int Kind; // 0 = passenger cushion, 1 = toilet seat
        internal Vector3 Cushion;
        internal Vector3 Forward;
        internal Vector3 Size;
        internal Vector3 Feet { get { return new Vector3(Cushion.x, 1.2029f, Cushion.z); } }
    }

    internal static class SeatData
    {
        internal static PassengerSeatDefinition[] Seats;
        internal static string Failure;
        private static bool attempted;
        private static bool completionObserved;
        private static Task<SeatProfileResult> worker;

        private sealed class RawSeat
        {
            internal int Kind;
            internal float CushionX, CushionY, CushionZ;
            internal float ForwardX, ForwardY, ForwardZ;
            internal float SizeX, SizeY, SizeZ;
        }

        private sealed class SeatProfileResult
        {
            internal RawSeat[] Seats;
            internal string Failure;
            internal long ElapsedMilliseconds;
        }

        internal static bool IsReady { get { Poll(); return Seats != null; } }

        internal static void BeginLoad(string modPath)
        {
            if (attempted) { Poll(); return; }
            attempted = true;
            string assetPath = Path.Combine(Application.dataPath, "resources.assets");
            worker = Task.Factory.StartNew<SeatProfileResult>(
                () => ReadProfile(modPath, assetPath), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        internal static bool Load(string modPath)
        {
            BeginLoad(modPath);
            Poll();
            return Seats != null;
        }

        // Must run on Unity's main thread. The worker only touches files and
        // managed buffers; Unity objects and logging are published here.
        internal static bool Poll()
        {
            if (worker == null || !worker.IsCompleted || completionObserved) return false;
            completionObserved = true;
            SeatProfileResult result;
            try { result = worker.Result; }
            catch (Exception error) { result = new SeatProfileResult { Failure = error.GetBaseException().Message }; }

            Failure = result.Failure;
            if (Failure == null)
            {
                RawSeat[] raw = result.Seats;
                Seats = new PassengerSeatDefinition[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                {
                    RawSeat value = raw[i];
                    Vector3 forward = new Vector3(value.ForwardX, value.ForwardY, value.ForwardZ);
                    Vector3 size = new Vector3(value.SizeX, value.SizeY, value.SizeZ);
                    if (size.x <= 0f || size.y <= 0f || size.z <= 0f || forward.sqrMagnitude < .1f)
                    {
                        Failure = "Invalid seat anchor in measured profile.";
                        Seats = null;
                        break;
                    }
                    forward.Normalize();
                    Seats[i] = new PassengerSeatDefinition
                    {
                        Index = i,
                        Kind = value.Kind,
                        Cushion = new Vector3(value.CushionX, value.CushionY, value.CushionZ),
                        Forward = forward,
                        Size = size
                    };
                }
            }
            if (Main.Entry != null && Main.Entry.Logger != null)
            {
                if (Failure == null)
                    Main.Entry.Logger.Log("Passenger seat profile loaded asynchronously: " + result.ElapsedMilliseconds + " ms");
                else
                    Main.Entry.Logger.Error("Passenger seat profile unavailable: " + Failure);
            }
            return true;
        }

        private static SeatProfileResult ReadProfile(string modPath, string source)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RawSeat[] seats;
                using (var stream = new GZipStream(File.OpenRead(Path.Combine(modPath, "data/stock-seats.bin.gz")), CompressionMode.Decompress))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != 13501) throw new InvalidDataException("Unknown seat profile format.");
                    string expected = ReadText(reader);
                    using (var sha = SHA256.Create())
                    using (var file = File.OpenRead(source))
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Installed passenger coach assets differ from the measured seat profile.");
                    }
                    int count = ReadCount(reader, 128);
                    seats = new RawSeat[count];
                    for (int i = 0; i < count; i++)
                    {
                        var seat = new RawSeat { Kind = reader.ReadInt32() };
                        seat.CushionX = reader.ReadSingle(); seat.CushionY = reader.ReadSingle(); seat.CushionZ = reader.ReadSingle();
                        seat.ForwardX = reader.ReadSingle(); seat.ForwardY = reader.ReadSingle(); seat.ForwardZ = reader.ReadSingle();
                        seat.SizeX = reader.ReadSingle(); seat.SizeY = reader.ReadSingle(); seat.SizeZ = reader.ReadSingle();
                        seats[i] = seat;
                    }
                }
                timer.Stop();
                return new SeatProfileResult { Seats = seats, ElapsedMilliseconds = timer.ElapsedMilliseconds };
            }
            catch (Exception error)
            {
                timer.Stop();
                return new SeatProfileResult { Failure = error.GetBaseException().Message, ElapsedMilliseconds = timer.ElapsedMilliseconds };
            }
        }

        private static int ReadCount(BinaryReader reader, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max) throw new InvalidDataException("Invalid seat profile size.");
            return count;
        }

        private static string ReadText(BinaryReader reader)
        {
            int count = ReadCount(reader, 128);
            return Encoding.ASCII.GetString(reader.ReadBytes(count));
        }

    }
}
