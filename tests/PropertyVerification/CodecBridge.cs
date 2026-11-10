#:project ../../src/CdBurner.Core/CdBurner.Core.csproj
#:property TargetFramework=net10.0-windows10.0.22621.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Line-oriented bridge that exposes the shipped CdBurner.Core framing, checksum and archive code to the
// Python property suite in this folder. One JSON request per stdin line, one JSON reply per stdout line.
// Binary values travel as base64. A failed call replies {"ok":false,"error":"<exception type>","message":...}
// so the suite can tell an explicit rejection from a silently wrong answer.
//
// Run by test_media_degradation.py as:  dotnet run CodecBridge.cs

using System.Text;
using System.Text.Json.Nodes;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };

stdout.WriteLine("READY");

string? line;
while ((line = stdin.ReadLine()) != null)
{
    if (line.Length == 0)
    {
        continue;
    }

    JsonObject reply;
    try
    {
        reply = await HandleAsync(JsonNode.Parse(line)!.AsObject());
        reply["ok"] = true;
    }
    catch (Exception ex)
    {
        reply = new JsonObject { ["ok"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message };
    }

    stdout.WriteLine(reply.ToJsonString());
}

static byte[] Bytes(JsonObject req, string name) => Convert.FromBase64String(req[name]!.GetValue<string>());

static string B64(byte[] value) => Convert.ToBase64String(value);

static JsonArray Strings(IEnumerable<string> values)
{
    var array = new JsonArray();
    foreach (string v in values)
    {
        array.Add(v);
    }
    return array;
}

static async Task<JsonObject> HandleAsync(JsonObject req)
{
    string op = req["op"]!.GetValue<string>();
    switch (op)
    {
        // ---- CD-ROM raw sector framing (sync, header, EDC, P/Q parity) ----
        case "mode1_encode":
            return new JsonObject
            {
                ["raw"] = B64(RawSectorCodec.Convert2048To2352Mode1(Bytes(req, "data"), req["lba"]!.GetValue<int>()))
            };

        case "mode2_encode":
        {
            var raw = new byte[RawSectorCodec.RawSectorSize];
            RawSectorCodec.Convert2048To2352Mode2Xa(Bytes(req, "data"), req["lba"]!.GetValue<int>(), raw);
            return new JsonObject { ["raw"] = B64(raw) };
        }

        case "sector_extract":
        {
            byte[] data = RawSectorCodec.Extract2048From2352(Bytes(req, "raw"), out bool edcValid);
            return new JsonObject { ["data"] = B64(data), ["edcValid"] = edcValid };
        }

        case "edc":
            return new JsonObject { ["edc"] = RawSectorCodec.ComputeEdc(Bytes(req, "data")) };

        // ---- CD-Text packs (18-byte packs, CRC-16) ----
        case "cdtext_build":
        {
            var tracks = new List<CdTextTrackMetadata>();
            foreach (JsonNode? title in req["titles"]!.AsArray())
            {
                tracks.Add(new CdTextTrackMetadata(tracks.Count + 1, title!.GetValue<string>()));
            }
            var album = new CdTextAlbumMetadata(req["album"]!.GetValue<string>(), Tracks: tracks);
            return new JsonObject { ["packs"] = B64(CdTextPackBuilder.BuildPacks(album)) };
        }

        case "cdtext_parse":
        {
            CdTextAlbumMetadata album = CdTextPackParser.ParsePacks(Bytes(req, "packs"));
            return new JsonObject
            {
                ["album"] = album.AlbumTitle,
                ["titles"] = Strings((album.Tracks ?? []).Select(t => t.Title))
            };
        }

        // ---- FLAC frame sync / CRC-8 / CRC-16 state machine ----
        case "flac_encode":
            return new JsonObject { ["flac"] = B64(FlacFileCodec.CreateFlacBytes(Bytes(req, "pcm"))) };

        case "flac_decode":
        {
            using var ms = new MemoryStream(Bytes(req, "flac"), writable: false);
            return new JsonObject { ["pcm"] = B64(FlacFileCodec.DecodeToRedBookPcm(ms)) };
        }

        // ---- Archive staging, SHA-256 manifest and span planning ----
        case "archive_stage":
        {
            ArchivePlan plan = ArchiveSpanPlanner.Plan(
                req["source"]!.GetValue<string>(), DiscMediaType.CdR, redundancyPercent: 0, setId: "propertyset");
            IReadOnlyList<string> discs = await ArchiveStager.StageAsync(plan, req["stagingRoot"]!.GetValue<string>(), par2Path: null);
            return new JsonObject { ["discs"] = Strings(discs), ["fileCount"] = plan.Discs.Sum(d => d.Files.Count) };
        }

        case "archive_verify":
        {
            ArchiveVerificationResult result = await ArchiveManifest.VerifyAsync(req["path"]!.GetValue<string>());
            return new JsonObject
            {
                ["intact"] = result.IsIntact,
                ["filesChecked"] = result.FilesChecked,
                ["missing"] = Strings(result.Missing),
                ["corrupt"] = Strings(result.Corrupt),
                ["hasRecoveryData"] = result.HasRecoveryData,
                ["setId"] = result.SetId
            };
        }

        case "plan":
        {
            var files = new List<ArchiveSourceFile>();
            foreach (JsonNode? size in req["sizes"]!.AsArray())
            {
                string name = $"f{files.Count:D6}";
                files.Add(new ArchiveSourceFile($@"X:\src\{name}", name, size!.GetValue<long>()));
            }

            var media = Enum.Parse<DiscMediaType>(req["media"]!.GetValue<string>());
            ArchivePlan plan = ArchiveSpanPlanner.Plan(files, media, req["redundancy"]!.GetValue<int>(), "propertyset");

            var discs = new JsonArray();
            foreach (ArchiveDiscPlan disc in plan.Discs)
            {
                discs.Add(new JsonObject
                {
                    ["number"] = disc.DiscNumber,
                    ["sectors"] = disc.DataSectors,
                    ["bytes"] = disc.DataBytes,
                    ["files"] = Strings(disc.Files.Select(f => f.RelativePath))
                });
            }

            return new JsonObject { ["usable"] = plan.UsableSectorsPerDisc, ["totalBytes"] = plan.TotalBytes, ["discs"] = discs };
        }

        case "file_sectors":
            return new JsonObject { ["sectors"] = CompilationCapacityCalculator.CalculateFileSectors(req["size"]!.GetValue<long>()) };

        // ---- PAR2 invocation contract (the suite runs par2 itself with exactly these arguments) ----
        case "par2_find":
            return new JsonObject { ["path"] = Par2Runner.FindPar2Executable() };

        case "par2_args":
            return new JsonObject
            {
                ["args"] = Strings(Par2Runner.BuildCreateArguments(
                    req["output"]!.GetValue<string>(), req["base"]!.GetValue<string>(), req["redundancy"]!.GetValue<int>()))
            };

        case "par2_repair":
        {
            Par2RepairResult repair = await Par2Runner.RepairAsync(req["par2"]!.GetValue<string>(), req["folder"]!.GetValue<string>());
            return new JsonObject
            {
                ["exitCode"] = repair.ExitCode,
                ["errorOutput"] = repair.ErrorOutput,
                ["backups"] = Strings(repair.BackupFiles),
                ["attempts"] = repair.Attempts
            };
        }

        default:
            throw new NotSupportedException($"Unknown bridge operation '{op}'.");
    }
}
