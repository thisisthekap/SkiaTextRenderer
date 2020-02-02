using System;
using System.Collections.Generic;
using SkiaSharp;
using System.Drawing;

namespace SkiaTextRenderer
{
    public class TextRenderer
    {
        private static readonly string[] NewLineCharacters = new[] { Environment.NewLine, UnicodeCharacters.NewLine.ToString(), UnicodeCharacters.CarriageReturn.ToString() };

        private static FontCache FontCache;
        private static readonly SKPaint TextPaint = new SKPaint();
        private static float LineHeight { get => TextPaint.TextSize; }
        private static FontStyle TextStyle;
        private static string Text;
        private static TextFormatFlags Flags;
        private static float MaxLineWidth;
        private static Rectangle Bounds = Rectangle.Empty;

        private static Size ContentSize = Size.Empty;
        private static float LeftPadding
        {
            get
            {
                if (Flags.HasFlag(TextFormatFlags.NoPadding))
                    return 0;

                if (Flags.HasFlag(TextFormatFlags.LeftAndRightPadding))
                    return (float)Math.Ceiling((TextPaint.FontSpacing / 6.0) * 2.0);

                if (Flags.HasFlag(TextFormatFlags.GlyphOverhangPadding))
                    return (float)Math.Ceiling(TextPaint.FontSpacing / 6.0);

                return 0;
            }
        }
        private static float RightPadding
        {
            get
            {
                if (Flags.HasFlag(TextFormatFlags.NoPadding))
                    return 0;

                if (Flags.HasFlag(TextFormatFlags.LeftAndRightPadding))
                    return (float)Math.Ceiling((TextPaint.FontSpacing / 6.0) * 2.5);

                if (Flags.HasFlag(TextFormatFlags.GlyphOverhangPadding))
                    return (float)Math.Ceiling((TextPaint.FontSpacing / 6.0) * 1.5);

                return 0;
            }
        }

        private static bool EnableWrap { get => (Flags & TextFormatFlags.NoClipping) == 0; }
        private static bool LineBreakWithoutSpaces { get => (Flags & TextFormatFlags.WordBreak) == 0; }

        private static int NumberOfLines;
        private static int TextDesiredHeight;
        private static List<float> LinesWidth = new List<float>();
        private static List<float> LinesOffsetX = new List<float>();
        private static int LetterOffsetY;

        class LetterInfo
        {
            public char Character;
            public bool Valid;
            public float PositionX;
            public float PositionY;
            public int LineIndex;
        }
        private static List<LetterInfo> LettersInfo = new List<LetterInfo>();

        private delegate int GetFirstCharOrWordLength(string textLine, int startIndex);

        private static void PrepareTextPaint(Font font)
        {
            FontCache = FontCache.GetCache(font.Typeface, font.Size);

            TextPaint.IsStroke = false;
            TextPaint.HintingLevel = SKPaintHinting.Normal;
            TextPaint.IsAutohinted = true; // Only for freetype
            TextPaint.IsEmbeddedBitmapText = true;
            TextPaint.DeviceKerningEnabled = true;

            TextPaint.Typeface = font.Typeface;
            TextPaint.TextSize = font.Size;

            if (font.Style == FontStyle.Italic)
                TextPaint.TextSkewX = -0.4f;
            else
                TextPaint.TextSkewX = 0;

            TextStyle = font.Style;
        }
        private static int GetFirstCharLength(string textLine, int startIndex)
        {
            return 1;
        }

        private static int GetFirstWordLength(string textLine, int startIndex)
        {
            int length = 0;
            float nextLetterX = 0;

            for (int index = startIndex; index < textLine.Length; ++index)
            {
                var character = textLine[index];

                if (character == UnicodeCharacters.NewLine || character == UnicodeCharacters.CarriageReturn
                    || (!Utils.IsUnicodeNonBreaking(character) && (Utils.IsUnicodeSpace(character) || Utils.IsCJKUnicode(character))))
                {
                    break;
                }

                if (!FontCache.GetLetterDefinitionForChar(character, out var letterDef))
                {
                    break;
                }

                if (MaxLineWidth > 0)
                {
                    var letterX = nextLetterX + letterDef.OffsetX;

                    if (letterX + letterDef.AdvanceX > MaxLineWidth)
                        break;
                }

                nextLetterX += letterDef.AdvanceX;

                length++;
            }

            if (length == 0 && textLine.Length > 0)
                length = 1;

            return length;
        }
        private static void RecordLetterInfo(SKPoint point, char character, int letterIndex, int lineIndex)
        {
            if (letterIndex >= LettersInfo.Count)
            {
                LettersInfo.Add(new LetterInfo());
            }

            LettersInfo[letterIndex].LineIndex = lineIndex;
            LettersInfo[letterIndex].Character = character;
            LettersInfo[letterIndex].Valid = FontCache.GetLetterDefinitionForChar(character, out var letterDef) && letterDef.ValidDefinition;
            LettersInfo[letterIndex].PositionX = point.X;
            LettersInfo[letterIndex].PositionY = point.Y;
        }
        private static void RecordPlaceholderInfo(int letterIndex, char character)
        {
            if (letterIndex >= LettersInfo.Count)
            {
                LettersInfo.Add(new LetterInfo());
            }

            LettersInfo[letterIndex].Character = character;
            LettersInfo[letterIndex].Valid = false;
        }
        private static void MultilineTextWrapByWord()
        {
            MultilineTextWrap(GetFirstWordLength);
        }
        private static void MultilineTextWrapByChar()
        {
            MultilineTextWrap(GetFirstCharLength);
        }
        private static void MultilineTextWrap(GetFirstCharOrWordLength nextTokenLength)
        {
            int textLength = Text.Length;
            int lineIndex = 0;
            float nextTokenX = 0;
            float nextTokenY = 0;
            float longestLine = 0;
            float letterRight = 0;
            float nextWhitespaceWidth = 0;
            FontLetterDefinition letterDef;
            SKPoint letterPosition = new SKPoint();
            bool nextChangeSize = true;

            for (int index = 0; index < textLength;)
            {
                char character = Text[index];
                if (character == UnicodeCharacters.NewLine)
                {
                    LinesWidth.Add(letterRight);
                    letterRight = 0;
                    lineIndex++;
                    nextTokenX = 0;
                    nextTokenY += LineHeight;
                    RecordPlaceholderInfo(index, character);
                    index++;
                    continue;
                }

                var tokenLen = nextTokenLength(Text, index);
                float tokenRight = letterRight;
                float nextLetterX = nextTokenX;
                float whitespaceWidth = nextWhitespaceWidth;
                bool newLine = false;
                for (int tmp = 0; tmp < tokenLen; ++tmp)
                {
                    int letterIndex = index + tmp;
                    character = Text[letterIndex];
                    if (character == UnicodeCharacters.CarriageReturn)
                    {
                        RecordPlaceholderInfo(letterIndex, character);
                        continue;
                    }

                    // \b - Next char not change x position
                    if (character == UnicodeCharacters.NextCharNoChangeX)
                    {
                        nextChangeSize = false;
                        RecordPlaceholderInfo(letterIndex, character);
                        continue;
                    }

                    if (!FontCache.GetLetterDefinitionForChar(character, out letterDef))
                    {
                        RecordPlaceholderInfo(letterIndex, character);
                        Console.WriteLine($"TextRenderer.MultilineTextWrap error: can't find letter definition in font file for letter: {character}");
                        continue;
                    }

                    var letterX = nextLetterX + letterDef.OffsetX;
                    if (EnableWrap && MaxLineWidth > 0 && nextTokenX > 0 && letterX + letterDef.AdvanceX > MaxLineWidth
                        && !Utils.IsUnicodeSpace(character) && nextChangeSize)
                    {
                        LinesWidth.Add(letterRight - whitespaceWidth);
                        nextWhitespaceWidth = 0f;
                        letterRight = 0f;
                        lineIndex++;
                        nextTokenX = 0f;
                        nextTokenY += LineHeight;
                        newLine = true;
                        break;
                    }
                    else
                    {
                        letterPosition.X = letterX;
                    }

                    letterPosition.Y = nextTokenY + letterDef.OffsetY;
                    RecordLetterInfo(letterPosition, character, letterIndex, lineIndex);

                    if (nextChangeSize)
                    {
                        var newLetterWidth = letterDef.AdvanceX;

                        nextLetterX += newLetterWidth;
                        tokenRight = nextLetterX;

                        if (Utils.IsUnicodeSpace(character))
                        {
                            nextWhitespaceWidth += newLetterWidth;
                        }
                        else
                        {
                            nextWhitespaceWidth = 0;
                        }
                    }

                    nextChangeSize = true;
                }

                if (newLine)
                {
                    continue;
                }

                nextTokenX = nextLetterX;
                letterRight = tokenRight;

                index += tokenLen;
            }

            if (LinesWidth.Count == 0)
            {
                LinesWidth.Add(letterRight);
                longestLine = letterRight;
            }
            else
            {
                LinesWidth.Add(letterRight - nextWhitespaceWidth);
                foreach (var lineWidth in LinesWidth)
                {
                    if (longestLine < lineWidth)
                        longestLine = lineWidth;
                }
            }

            NumberOfLines = lineIndex + 1;
            TextDesiredHeight = (int)(NumberOfLines * LineHeight);

            ContentSize.Width = (int)(longestLine + LeftPadding + RightPadding);
            ContentSize.Height = TextDesiredHeight;
        }

        private static void ComputeAlignmentOffset()
        {
            LinesOffsetX.Clear();

            if (Flags.HasFlag(TextFormatFlags.HorizontalCenter))
            {
                foreach (var lineWidth in LinesWidth)
                    LinesOffsetX.Add((Bounds.Width - lineWidth) / 2f);
            }
            else if (Flags.HasFlag(TextFormatFlags.Right))
            {
                foreach (var lineWidth in LinesWidth)
                    LinesOffsetX.Add(Bounds.Width - lineWidth);
            }
            else
            {
                for (int i = 0; i < NumberOfLines; i++)
                    LinesOffsetX.Add(0);
            }

            if (Flags.HasFlag(TextFormatFlags.VerticalCenter))
            {
                LetterOffsetY = (Bounds.Height - TextDesiredHeight) / 2;
            }
            else if (Flags.HasFlag(TextFormatFlags.Bottom))
            {
                LetterOffsetY = Bounds.Height - TextDesiredHeight;
            }
            else
            {
                LetterOffsetY = 0;
            }
        }

        private static void AlignText()
        {
            if (string.IsNullOrEmpty(Text))
            {
                ContentSize = Size.Empty;
                return;
            }

            FontCache.PrepareLetterDefinitions(Text);

            TextDesiredHeight = 0;
            LinesWidth.Clear();
            LettersInfo.Clear();

            if (MaxLineWidth > 0 && !LineBreakWithoutSpaces)
                MultilineTextWrapByWord();
            else
                MultilineTextWrapByChar();
        }

        public static Size MeasureText(string text, Font font)
        {
            return MeasureText(text, font, 0, TextFormatFlags.Default);
        }
        public static Size MeasureText(string text, Font font, int maxLineWidth)
        {
            return MeasureText(text, font, maxLineWidth, TextFormatFlags.Default);
        }

        public static Size MeasureText(string text, Font font, float maxLineWidth, TextFormatFlags flags)
        {
            Text = text;
            Flags = flags;
            MaxLineWidth = maxLineWidth;

            PrepareTextPaint(font);

            AlignText();

            return ContentSize;
        }

        private static HashSet<int> LinesHadDrawedUnderlines = new HashSet<int>();

        public static void DrawText(SKCanvas canvas, string text, Font font, Rectangle bounds, SKColor foreColor, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var textAligned = (text == Text && MaxLineWidth == bounds.Width && Flags == flags) &&
                (TextPaint.Typeface == font.Typeface && TextPaint.TextSize == font.Size && TextStyle == font.Style);

            if (!textAligned)
            {
                Text = text;
                Flags = flags;
                MaxLineWidth = bounds.Width;

                PrepareTextPaint(font);
                AlignText();
            }

            Bounds = bounds;
            ComputeAlignmentOffset();

            TextPaint.Color = foreColor;

            SKPoint[] glyphPositions = new SKPoint[Text.Length];

            if (TextStyle == FontStyle.Underline || TextStyle == FontStyle.Strikeout)
                LinesHadDrawedUnderlines.Clear();

            for (int i = 0; i < Text.Length; i++)
            {
                var letterInfo = LettersInfo[i];
                var pos = new SKPoint();

                if (!FontCache.GetLetterDefinitionForChar(letterInfo.Character, out var letterDef))
                    continue;

                pos.X = letterInfo.PositionX + LinesOffsetX[letterInfo.LineIndex] + bounds.X;
                if (!Flags.HasFlag(TextFormatFlags.HorizontalCenter))
                    pos.X += LeftPadding;

                pos.Y = letterInfo.PositionY + LetterOffsetY + bounds.Y;

                if (Flags.HasFlag(TextFormatFlags.ExternalLeading))
                    pos.Y += TextPaint.FontMetrics.Leading;

                glyphPositions[i] = pos;

                if (TextStyle == FontStyle.Underline || TextStyle == FontStyle.Strikeout)
                {
                    if (LinesHadDrawedUnderlines.Contains(letterInfo.LineIndex))
                        continue;

                    pos.Y += TextStyle == FontStyle.Underline ? (TextPaint.FontMetrics.UnderlinePosition ?? 0) : (TextPaint.FontMetrics.StrikeoutPosition ?? 0);
                    canvas.DrawLine(new SKPoint(pos.X, pos.Y), new SKPoint(pos.X + LinesWidth[i], pos.Y), TextPaint);

                    LinesHadDrawedUnderlines.Add(letterInfo.LineIndex);
                }
            }

            canvas.DrawPositionedText(Text, glyphPositions, TextPaint);
        }
    }
}