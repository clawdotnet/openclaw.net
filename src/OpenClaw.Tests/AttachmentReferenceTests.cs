using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// P1 of the attachment-by-reference design: marker lines are lifted out of
/// <see cref="ChatTurn.Content"/> into <see cref="ChatAttachmentRef"/> and rendered back at the
/// provider boundary.
/// </summary>
/// <remarks>
/// The point of these tests is <em>equivalence</em>, not new behaviour. Every case here would have
/// passed against the pre-P1 marker code too — if one of them starts failing, the reference path has
/// drifted from what the marker path used to do, which is the one thing P1 promised not to change.
/// See docs/superpowers/specs/2026-10-19-attachment-by-reference-design.md.
/// </remarks>
public sealed class AttachmentReferenceTests
{
    private const string ImageUrl = "https://example.test/a.png";
    private const string TelegramFileId = "AgACAgIAAxkBAAIB";

    [Fact]
    public void Extract_lifts_marker_lines_and_keeps_the_prose()
    {
        var message = $"look at this\n[IMAGE_URL:{ImageUrl}]\nthanks";

        var (content, attachments) = AttachmentReferences.Extract(message);

        Assert.Equal("look at this\nthanks", content);
        var attachment = Assert.Single(attachments!);
        Assert.Equal(MediaMarkerKind.ImageUrl, attachment.Kind);
        Assert.Equal(ImageUrl, attachment.Value);
        Assert.Equal("image/*", attachment.MimeType);
        Assert.Equal("a.png", attachment.FileName);
        Assert.Null(attachment.AssetId);
        Assert.Equal(0, attachment.SizeBytes);
    }

    /// <summary>
    /// A reference that points at a media-cache file carries that file's asset id, which is what makes a
    /// placeholder or a log line correlatable with a file on disk. The id is recovered from the file name
    /// the cache generated, not stored separately — see <see cref="AttachmentReferences.TryGetMediaAssetId"/>.
    /// </summary>
    [Fact]
    public void Extract_records_the_asset_id_of_a_cached_file()
    {
        var cached = $"/var/openclaw/media-cache/{AttachmentReferences.MediaAssetIdPrefix}0123456789abcdef.png";

        var (_, attachments) = AttachmentReferences.Extract($"[IMAGE_PATH:{cached}]");

        var attachment = Assert.Single(attachments!);
        Assert.Equal($"{AttachmentReferences.MediaAssetIdPrefix}0123456789abcdef", attachment.AssetId);
        Assert.Equal("media_0123456789abcdef.png", attachment.FileName);
    }

    /// <summary>
    /// The id is only recognised in the shape the cache actually produces. A user file that happens to
    /// be called <c>media_photo.png</c> must not turn into a reference pointing at nothing.
    /// </summary>
    [Theory]
    [InlineData("media_photo.png")]
    [InlineData("media_0123456789ABCDEF.png")]
    [InlineData("media_0123456789abcde.png")]
    [InlineData("0123456789abcdef.png")]
    public void TryGetMediaAssetId_rejects_anything_that_is_not_cache_shaped(string fileName)
    {
        Assert.False(AttachmentReferences.TryGetMediaAssetId($"/tmp/openclaw/{fileName}", out var assetId));
        Assert.Equal("", assetId);
    }

    [Fact]
    public void TryGetMediaAssetId_accepts_a_path_with_or_without_an_extension()
    {
        Assert.True(AttachmentReferences.TryGetMediaAssetId("/cache/media_0123456789abcdef", out var bare));
        Assert.Equal("media_0123456789abcdef", bare);

        Assert.True(AttachmentReferences.TryGetMediaAssetId("https://host/media_0123456789abcdef.jpg", out var withExtension));
        Assert.Equal("media_0123456789abcdef", withExtension);
    }

    /// <summary>
    /// Null attachments is the load-bearing signal for "legacy or attachment-free". An empty list
    /// would make an old session indistinguishable from a turn that genuinely had no attachments and
    /// would push the read path down the reference branch with nothing to render.
    /// </summary>
    [Fact]
    public void Extract_returns_the_original_text_and_null_when_there_are_no_markers()
    {
        const string message = "just prose, no media";

        var (content, attachments) = AttachmentReferences.Extract(message);

        Assert.Same(message, content);
        Assert.Null(attachments);
    }

    [Fact]
    public void Extract_keeps_empty_prose_when_the_message_is_only_attachments()
    {
        var (content, attachments) = AttachmentReferences.Extract($"[IMAGE_URL:{ImageUrl}]");

        Assert.Equal("", content);
        Assert.Single(attachments!);
    }

    [Fact]
    public void CreateUserTurn_strips_markers_and_sets_the_role()
    {
        var turn = AttachmentReferences.CreateUserTurn($"hi\n[IMAGE_URL:{ImageUrl}]");

        Assert.Equal("user", turn.Role);
        Assert.Equal("hi", turn.Content);
        Assert.Single(turn.Attachments!);
    }

    [Theory]
    [InlineData(MediaMarkerKind.ImageUrl, ImageUrl)]
    [InlineData(MediaMarkerKind.ImagePath, "/tmp/openclaw/picture.png")]
    [InlineData(MediaMarkerKind.FileUrl, "https://example.test/doc.pdf")]
    [InlineData(MediaMarkerKind.FilePath, "/tmp/openclaw/doc.pdf")]
    [InlineData(MediaMarkerKind.VideoUrl, "https://example.test/clip.mp4")]
    [InlineData(MediaMarkerKind.AudioUrl, "https://example.test/voice.mp3")]
    [InlineData(MediaMarkerKind.DocumentUrl, "https://example.test/report.docx")]
    [InlineData(MediaMarkerKind.StickerUrl, "https://example.test/sticker.webp")]
    [InlineData(MediaMarkerKind.TelegramImageFileId, TelegramFileId)]
    [InlineData(MediaMarkerKind.TelegramVideoFileId, TelegramFileId)]
    [InlineData(MediaMarkerKind.TelegramAudioFileId, TelegramFileId)]
    [InlineData(MediaMarkerKind.TelegramDocumentFileId, TelegramFileId)]
    [InlineData(MediaMarkerKind.TelegramStickerFileId, TelegramFileId)]
    public void Marker_line_round_trips_through_a_reference(MediaMarkerKind kind, string value)
    {
        var original = MediaMarkerProtocol.Format(new MediaMarker(kind, value));

        var (_, attachments) = AttachmentReferences.Extract(original);
        var rendered = AttachmentReferences.ToMarkerLine(Assert.Single(attachments!));

        Assert.Equal(original, rendered);
        Assert.True(MediaMarkerProtocol.TryParseMarker(rendered, out var reparsedMarker));
        Assert.Equal(kind, reparsedMarker.Kind);
        Assert.Equal(value, reparsedMarker.Value);
    }

    /// <summary>
    /// The whole phase rests on this: a turn stored as references must produce exactly the content
    /// list the turn would have produced while its markers were still in <c>Content</c>.
    /// </summary>
    [Fact]
    public void Reference_path_matches_the_marker_path_part_for_part()
    {
        var message = string.Join(
            "\n",
            "please look",
            $"[IMAGE_URL:{ImageUrl}]",
            "[IMAGE_PATH:/tmp/openclaw/local.png]",
            "[FILE_PATH:/tmp/openclaw/local.pdf]",
            $"[AUDIO_URL:https://example.test/voice.mp3]",
            // Degrades to plain text on both paths: not an absolute URI, but a valid relative one.
            $"[IMAGE:telegram:file_id={TelegramFileId}]");

        var legacy = AttachmentContentRenderer.Build(message, attachments: null);

        var (content, attachments) = AttachmentReferences.Extract(message);
        var referenced = AttachmentContentRenderer.Build(content, attachments);

        Assert.Equal(Describe(legacy), Describe(referenced));
    }

    [Fact]
    public void Marker_only_message_matches_the_marker_path()
    {
        var message = $"[IMAGE_PATH:/tmp/openclaw/only.png]\n[FILE_URL:https://example.test/x.pdf]";

        var legacy = AttachmentContentRenderer.Build(message, attachments: null);
        var (content, attachments) = AttachmentReferences.Extract(message);
        var referenced = AttachmentContentRenderer.Build(content, attachments);

        Assert.Equal(Describe(legacy), Describe(referenced));
        Assert.Equal(2, referenced.Count);
    }

    [Fact]
    public void Attachment_free_turn_still_produces_a_single_text_part()
    {
        var contents = AttachmentContentRenderer.Build("hello", attachments: null);

        var text = Assert.IsType<TextContent>(Assert.Single(contents));
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public void Empty_turn_falls_back_to_one_text_part_like_before()
    {
        var contents = AttachmentContentRenderer.Build("", attachments: null);

        Assert.IsType<TextContent>(Assert.Single(contents));
    }

    [Fact]
    public void Relative_reference_values_degrade_to_text_on_both_paths()
    {
        var message = $"[IMAGE:telegram:file_id={TelegramFileId}]";

        var legacy = Assert.Single(AttachmentContentRenderer.Build(message, attachments: null));
        var (content, attachments) = AttachmentReferences.Extract(message);
        var referenced = Assert.Single(AttachmentContentRenderer.Build(content, attachments));

        var legacyText = Assert.IsType<TextContent>(legacy);
        var referencedText = Assert.IsType<TextContent>(referenced);
        Assert.Equal(legacyText.Text, referencedText.Text);
        Assert.Equal(TelegramFileId, referencedText.Text);
    }

    /// <summary>
    /// MAF sends text only, so its reference turns have to be rendered back to marker lines. This
    /// pins the shape of that rendering: prose first, marker lines after, nothing duplicated.
    /// </summary>
    [Fact]
    public void Legacy_text_rendering_appends_marker_lines_after_the_prose()
    {
        var turn = AttachmentReferences.CreateUserTurn($"hi\n[IMAGE_URL:{ImageUrl}]");

        var rendered = AttachmentReferences.RenderMarkerText(turn);

        Assert.Equal($"hi\n[IMAGE_URL:{ImageUrl}]", rendered);
    }

    [Fact]
    public void Legacy_text_rendering_leaves_attachment_free_turns_alone()
    {
        var turn = new ChatTurn { Role = "user", Content = "plain" };

        Assert.Equal("plain", AttachmentReferences.RenderMarkerText(turn));
    }

    [Fact]
    public void Json_round_trip_preserves_references()
    {
        var turn = AttachmentReferences.CreateUserTurn($"hi\n[IMAGE_PATH:/tmp/openclaw/p.png]");

        var json = JsonSerializer.Serialize(turn, CoreJsonContext.Default.ChatTurn);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ChatTurn);

        var attachment = Assert.Single(restored!.Attachments!);
        Assert.Equal(MediaMarkerKind.ImagePath, attachment.Kind);
        Assert.Equal("/tmp/openclaw/p.png", attachment.Value);
        Assert.Equal("image/*", attachment.MimeType);
        Assert.Equal("hi", restored.Content);
    }

    /// <summary>
    /// A turn without attachments must not grow the payload: sessions are rewritten in full on every
    /// save, so an always-present empty list would inflate every ledger file.
    /// </summary>
    /// <remarks>
    /// Case-insensitive because <see cref="CoreJsonContext"/> camel-cases names — asserting on
    /// "Attachments" would pass for the wrong reason (the key is "attachments").
    /// </remarks>
    [Fact]
    public void Json_omits_attachments_when_absent()
    {
        var turn = new ChatTurn { Role = "user", Content = "plain" };

        var json = JsonSerializer.Serialize(turn, CoreJsonContext.Default.ChatTurn);

        Assert.DoesNotContain("attachment", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_written_before_this_field_existed_reads_back_as_no_attachments()
    {
        // Persisted sessions are camelCase: CoreJsonContext sets PropertyNamingPolicy = CamelCase.
        const string legacyJson =
            """{"role":"user","content":"hi","timestamp":"2026-01-01T00:00:00+00:00","toolCalls":null}""";

        var turn = JsonSerializer.Deserialize(legacyJson, CoreJsonContext.Default.ChatTurn);

        Assert.NotNull(turn);
        Assert.Null(turn!.Attachments);
        Assert.Equal("hi", turn.Content);
    }

    /// <summary>
    /// Persisted history stays readable for consumers that only know <c>role</c> and <c>content</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skill pipelines feed <c>session.History</c> to external scripts as JSON — <c>history-explorer</c>
    /// reads <c>turn["role"]</c> and <c>turn["content"]</c> and ignores everything else. Attachment
    /// references are therefore an <em>additive</em> change: a new field appears, and the marker text
    /// that used to sit inside <c>content</c> is gone.
    /// </para>
    /// <para>
    /// The second half is the part that could break something: a consumer that matched on
    /// <c>[IMAGE_URL:…]</c> inside <c>content</c> would silently stop matching. Every skill in this
    /// repository and in the deployed managed-skills root was checked and none does — this test is what
    /// keeps that true for the format itself, rather than for the one afternoon it was checked.
    /// </para>
    /// </remarks>
    [Fact]
    public void Persisted_history_stays_readable_for_role_and_content_consumers()
    {
        var cached = $"/var/openclaw/media-cache/{AttachmentReferences.MediaAssetIdPrefix}0123456789abcdef.png";
        var history = new List<ChatTurn> { AttachmentReferences.CreateUserTurn($"hi\n[IMAGE_PATH:{cached}]") };

        var json = JsonSerializer.Serialize(history, CoreJsonContext.Default.ListChatTurn);
        using var document = JsonDocument.Parse(json);
        var turn = document.RootElement[0];

        // The two keys every existing consumer relies on.
        Assert.Equal("user", turn.GetProperty("role").GetString());
        var content = turn.GetProperty("content").GetString();
        Assert.Equal("hi", content);

        // No marker left behind, so nothing can match on one.
        Assert.DoesNotContain("[IMAGE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("[FILE", json, StringComparison.Ordinal);

        // The attachment is discoverable, with the id the inliner and operators both use.
        var attachment = Assert.Single(turn.GetProperty("attachments").EnumerateArray());

        // Kind is written by name, so a consumer (or an operator reading a session file) does not need a
        // mapping table to know the media class. History from before the converter still carries the
        // ordinal and still reads — MediaMarkerKindPersistenceTests pins both forms.
        Assert.Equal(nameof(MediaMarkerKind.ImagePath), attachment.GetProperty("kind").GetString());
        Assert.Equal(cached, attachment.GetProperty("value").GetString());
        Assert.Equal($"{AttachmentReferences.MediaAssetIdPrefix}0123456789abcdef", attachment.GetProperty("assetId").GetString());
    }

    /// <summary>
    /// History compaction flattens turns to text. Before references existed the marker line carried
    /// this fact — and leaked the URL into the summary; the note keeps the fact and drops the URL.
    /// </summary>
    [Fact]
    public void Summarization_note_records_media_without_leaking_the_url()
    {
        var turn = AttachmentReferences.CreateUserTurn($"hi\n[IMAGE_URL:{ImageUrl}]");

        var note = AttachmentReferences.SummarizationNote(turn);

        Assert.Equal(" [1 attachment(s) omitted]", note);
        Assert.DoesNotContain(ImageUrl, note, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarization_note_is_empty_for_attachment_free_turns()
    {
        var turn = new ChatTurn { Role = "user", Content = "plain" };

        Assert.Equal("", AttachmentReferences.SummarizationNote(turn));
    }

    private static List<string> Describe(IEnumerable<AIContent> contents)
    {
        var described = new List<string>();
        foreach (var content in contents)
        {
            described.Add(content switch
            {
                TextContent text => $"text:{text.Text}",
                UriContent uri => $"uri:{uri.MediaType}:{uri.Uri}",
                _ => $"other:{content.GetType().Name}"
            });
        }

        return described;
    }
}
