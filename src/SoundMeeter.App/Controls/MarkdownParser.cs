using System.Text;

namespace SoundMeeter.Controls
{
    /// <summary>Блок разобранного markdown: абзац, заголовок, пункт списка и т. п.</summary>
    public abstract class MarkdownBlock
    {
    }

    /// <summary>Заголовок <c>#</c>…<c>######</c>. Уровень ограничен шестью — дальше он ничего не меняет.</summary>
    public sealed class MarkdownHeading : MarkdownBlock
    {
        public MarkdownHeading(int level, IReadOnlyList<MarkdownSpan> spans)
        {
            Level = level;
            Spans = spans;
        }

        public int Level { get; }
        public IReadOnlyList<MarkdownSpan> Spans { get; }
    }

    /// <summary>Обычный текст. Переносы строк внутри абзаца — мягкие, кроме `LineBreak`.</summary>
    public sealed class MarkdownParagraph : MarkdownBlock
    {
        public MarkdownParagraph(IReadOnlyList<MarkdownSpan> spans) => Spans = spans;

        public IReadOnlyList<MarkdownSpan> Spans { get; }
    }

    /// <summary>Маркированный или нумерованный список вместе со вложенными блоками.</summary>
    public sealed class MarkdownList : MarkdownBlock
    {
        public MarkdownList(bool ordered, IReadOnlyList<MarkdownListItem> items)
        {
            Ordered = ordered;
            Items = items;
        }

        public bool Ordered { get; }
        public IReadOnlyList<MarkdownListItem> Items { get; }
    }

    /// <summary>
    /// Пункт списка. <see cref="Number"/> заполнен только у нумерованного,
    /// у вложенных пунктов свой номер не рисуется.
    /// </summary>
    public sealed class MarkdownListItem : MarkdownBlock
    {
        public MarkdownListItem(IReadOnlyList<MarkdownSpan> spans, IReadOnlyList<MarkdownBlock> children,
            int number, bool? checkedState = null)
        {
            Spans = spans;
            Children = children;
            Number = number;
            CheckedState = checkedState;
        }

        public IReadOnlyList<MarkdownSpan> Spans { get; }

        /// <summary>Вложенный список, код или абзац, продолжающие пункт.</summary>
        public IReadOnlyList<MarkdownBlock> Children { get; }

        /// <summary>Номер пункта для нумерованного списка, иначе 0.</summary>
        public int Number { get; }

        /// <summary>Галочка задачи (<c>- [x]</c>): true — сделано, false — нет, null — не задача.</summary>
        public bool? CheckedState { get; }
    }

    /// <summary>Ограждённый или отступный блок кода — показывается дословно, без разметки.</summary>
    public sealed class MarkdownCode : MarkdownBlock
    {
        public MarkdownCode(string code, string language)
        {
            Code = code;
            Language = language;
        }

        public string Code { get; }
        public string Language { get; }
    }

    /// <summary>Цитата <c>&gt;</c>: содержимое разбирается как обычные блоки.</summary>
    public sealed class MarkdownQuote : MarkdownBlock
    {
        public MarkdownQuote(IReadOnlyList<MarkdownBlock> blocks) => Blocks = blocks;

        public IReadOnlyList<MarkdownBlock> Blocks { get; }
    }

    /// <summary>Горизонтальная линия <c>---</c>.</summary>
    public sealed class MarkdownRule : MarkdownBlock
    {
    }

    /// <summary>Кусок текста внутри блока.</summary>
    public abstract class MarkdownSpan
    {
    }

    /// <summary>Обычный текст.</summary>
    public sealed class MarkdownText : MarkdownSpan
    {
        public MarkdownText(string text) => Text = text;

        public string Text { get; }
    }

    /// <summary>`код` внутри строки — моноширинный, без разметки внутри.</summary>
    public sealed class MarkdownCodeSpan : MarkdownSpan
    {
        public MarkdownCodeSpan(string text) => Text = text;

        public string Text { get; }
    }

    /// <summary>Полужирный, курсив или зачёркнутый текст; вложенные стили разрешены.</summary>
    public sealed class MarkdownEmphasis : MarkdownSpan
    {
        public MarkdownEmphasis(bool bold, bool italic, bool strike, IReadOnlyList<MarkdownSpan> spans)
        {
            Bold = bold;
            Italic = italic;
            Strike = strike;
            Spans = spans;
        }

        public bool Bold { get; }
        public bool Italic { get; }
        public bool Strike { get; }
        public IReadOnlyList<MarkdownSpan> Spans { get; }
    }

    /// <summary>Ссылка: подпись может быть размеченной, адрес — как есть.</summary>
    public sealed class MarkdownLink : MarkdownSpan
    {
        public MarkdownLink(IReadOnlyList<MarkdownSpan> spans, string url)
        {
            Spans = spans;
            Url = url;
        }

        public IReadOnlyList<MarkdownSpan> Spans { get; }
        public string Url { get; }
    }

    /// <summary>Жёсткий перенос строки: два пробела в конце, `\` в конце или тег <c>&lt;br&gt;</c>.</summary>
    public sealed class MarkdownLineBreak : MarkdownSpan
    {
    }

    /// <summary>
    /// Разбор markdown в блоки. Ровно тот подмножество, которое встречается в описаниях
    /// релизов: заголовки, абзацы, вложенные списки, `код`, цитаты, линии, ссылки и
    /// картинки (картинка отдаётся подписью alt — тянуть графику из интернета в диалог
    /// обновления незачем).
    ///
    /// Полноценный CommonMark не нужен: подключать Markdig ради окна с текстом,
    /// который пишет сам автор релиза, — лишняя зависимость в переносимой сборке.
    /// Разбор строковый, без состояния между вызовами.
    /// </summary>
    public static class MarkdownParser
    {
        public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
        {
            var lines = PrepareLines(markdown);
            var definitions = CollectLinkDefinitions(lines);

            var blocks = new List<MarkdownBlock>();
            ParseBlocks(lines, definitions, blocks);
            return blocks;
        }

        /// <summary>Весь текст span'ов одной строкой — для подписи картинки и тестов.</summary>
        public static string PlainText(IReadOnlyList<MarkdownSpan> spans)
        {
            var text = new StringBuilder();
            AppendPlainText(spans, text);
            return text.ToString();
        }

        private static void AppendPlainText(IReadOnlyList<MarkdownSpan> spans, StringBuilder text)
        {
            foreach (var span in spans)
            {
                switch (span)
                {
                    case MarkdownText plain:
                        text.Append(plain.Text);
                        break;
                    case MarkdownCodeSpan code:
                        text.Append(code.Text);
                        break;
                    case MarkdownEmphasis emphasis:
                        AppendPlainText(emphasis.Spans, text);
                        break;
                    case MarkdownLink link:
                        AppendPlainText(link.Spans, text);
                        break;

                    // Перенос строки в одном тексте — пробел: иначе подпись картинки
                    // склеила бы строки.
                    case MarkdownLineBreak:
                        text.Append(' ');
                        break;
                }
            }
        }

        #region Разбор блоков

        private static void ParseBlocks(List<string> lines, Dictionary<string, string> definitions,
            List<MarkdownBlock> output)
        {
            int i = 0;
            while (i < lines.Count)
            {
                string line = lines[i];

                if (IsBlank(line))
                {
                    i++;
                    continue;
                }

                if (TryFence(line, out char fence, out int fenceLength, out string language))
                {
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count && !IsClosingFence(lines[i], fence, fenceLength))
                    {
                        code.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Count) i++;
                    output.Add(new MarkdownCode(string.Join("\n", code), language));
                    continue;
                }

                if (TryHeading(line, out int level, out string heading))
                {
                    output.Add(new MarkdownHeading(level, ParseInlines(heading, definitions)));
                    i++;
                    continue;
                }

                if (IsRule(line))
                {
                    output.Add(new MarkdownRule());
                    i++;
                    continue;
                }

                if (TryQuote(line, out string quoted))
                {
                    var inner = new List<string> { quoted };
                    i++;
                    while (i < lines.Count && TryQuote(lines[i], out string more))
                    {
                        inner.Add(more);
                        i++;
                    }

                    var quotedBlocks = new List<MarkdownBlock>();
                    ParseBlocks(inner, definitions, quotedBlocks);
                    output.Add(new MarkdownQuote(quotedBlocks));
                    continue;
                }

                if (TryListMarker(line, out _))
                {
                    var list = ParseList(lines, i, definitions, out MarkdownList parsed);
                    output.Add(parsed);
                    i += list;
                    continue;
                }

                if (IsIndentedCode(line))
                {
                    var code = new List<string>();
                    while (i < lines.Count && (IsIndentedCode(lines[i]) || IsBlank(lines[i])))
                    {
                        // Пустые строки внутри блока кода относятся к нему, пока дальше
                        // есть ещё indented-строки: иначе код из лога распался бы на части.
                        if (IsBlank(lines[i]))
                        {
                            int next = i + 1;
                            if (next >= lines.Count || !IsIndentedCode(lines[next])) break;
                        }

                        code.Add(lines[i].Length > 4 ? lines[i][4..] : lines[i].TrimStart());
                        i++;
                    }

                    output.Add(new MarkdownCode(string.Join("\n", code).TrimEnd('\n'), ""));
                    continue;
                }

                var paragraph = new List<string>();
                while (i < lines.Count && !IsBlank(lines[i]) && !StartsBlock(lines[i]))
                {
                    paragraph.Add(lines[i]);
                    i++;
                }

                // Строка, которая не началась ни одним блоком, обязана попасть в абзац:
                // иначе разбор встал бы на месте.
                if (paragraph.Count == 0)
                {
                    paragraph.Add(lines[i]);
                    i++;
                }

                output.Add(new MarkdownParagraph(ParseInlines(string.Join("\n", paragraph), definitions)));
            }
        }

        private readonly record struct ListMarker(int Indent, int ContentColumn, bool Ordered);

        /// <summary>
        /// Разбирает список, начиная с первой строки пункта. Возвращает, сколько строк
        /// он занял: пункты разных уровней вложенности — это один список, а список
        /// другого типа (маркированный после нумерованного) — уже следующий блок.
        /// </summary>
        private static int ParseList(List<string> lines, int start, Dictionary<string, string> definitions,
            out MarkdownList list)
        {
            TryListMarker(lines[start], out ListMarker first);
            bool ordered = first.Ordered;

            var items = new List<MarkdownListItem>();
            int i = start;

            while (i < lines.Count)
            {
                string line = lines[i];

                if (IsBlank(line))
                {
                    // Пустая строка внутри списка: список продолжается, только если
                    // следующая непустая строка — его пункт того же уровня.
                    int next = NextContent(lines, i);
                    if (next < lines.Count && TryListMarker(lines[next], out ListMarker after)
                        && after.Ordered == ordered && after.Indent >= first.Indent)
                    {
                        i = next;
                        continue;
                    }

                    break;
                }

                if (!TryListMarker(line, out ListMarker marker)) break;
                if (marker.Ordered != ordered || marker.Indent < first.Indent) break;

                int column = marker.ContentColumn;
                var body = new List<string> { line[column..] };
                i++;

                while (i < lines.Count)
                {
                    string next = lines[i];

                    if (IsBlank(next))
                    {
                        int look = NextContent(lines, i);
                        if (look < lines.Count && IndentOf(lines[look]) >= column)
                        {
                            body.Add("");
                            i++;
                            continue;
                        }

                        break;
                    }

                    // Вложенный пункт или продолжение с отступом: сдвигаем к началу тела,
                    // иначе вложенность не видна.
                    if (IndentOf(next) >= column)
                    {
                        body.Add(next[column..]);
                        i++;
                        continue;
                    }

                    // Новый блок (заголовок, ограда, цитата, линия, другой список) —
                    // это уже не продолжение пункта.
                    if (StartsBlock(next)) break;

                    // «Ленивое» продолжение: текст без отступа, относящийся к пункту.
                    body.Add(next.TrimStart());
                    i++;
                }

                items.Add(BuildItem(body, ordered ? items.Count + 1 : 0, definitions));
            }

            list = new MarkdownList(ordered, items);
            return i - start;
        }

        private static MarkdownListItem BuildItem(List<string> body, int number,
            Dictionary<string, string> definitions)
        {
            var blocks = new List<MarkdownBlock>();
            ParseBlocks(body, definitions, blocks);

            IReadOnlyList<MarkdownSpan> spans = [];
            var children = blocks;

            // Текст пункта — первый абзац; всё после него (вложенный список, код)
            // рисуется отдельно. Если первого абзаца нет, весь разобранный список
            // считается содержимым вложенности.
            if (blocks.Count > 0 && blocks[0] is MarkdownParagraph first)
            {
                spans = first.Spans;
                children = blocks.Skip(1).ToList();
            }

            spans = TakeTaskMarker(spans, out bool? checkedState);
            return new MarkdownListItem(spans, children, number, checkedState);
        }

        /// <summary>
        /// Задача <c>- [x] проверить</c>: галочка уезжает в свойство пункта, а из текста
        /// убирается — иначе в списке релизов остаётся мусор в квадратных скобках.
        /// </summary>
        private static IReadOnlyList<MarkdownSpan> TakeTaskMarker(IReadOnlyList<MarkdownSpan> spans,
            out bool? checkedState)
        {
            checkedState = null;
            if (spans.Count == 0 || spans[0] is not MarkdownText first) return spans;

            string[] markers = ["[ ]", "[x]", "[X]"];
            foreach (string marker in markers)
            {
                if (!first.Text.StartsWith(marker, StringComparison.Ordinal)) continue;

                string rest = first.Text[marker.Length..].TrimStart();
                checkedState = marker != "[ ]";
                return rest.Length > 0
                    ? new[] { new MarkdownText(rest) }.Concat(spans.Skip(1)).ToList()
                    : spans.Skip(1).ToList();
            }

            return spans;
        }

        private static int NextContent(List<string> lines, int from)
        {
            int i = from;
            while (i < lines.Count && IsBlank(lines[i])) i++;
            return i;
        }

        private static bool TryFence(string line, out char fence, out int length, out string language)
        {
            fence = '\0';
            length = 0;
            language = "";

            int i = IndentOf(line);
            if (i >= line.Length || (line[i] != '`' && line[i] != '~')) return false;

            int start = i;
            fence = line[start];
            while (i < line.Length && line[i] == fence) i++;
            length = i - start;
            if (length < 3) return false;

            language = line[i..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
                ? parts[0]
                : "";
            return true;
        }

        private static bool IsClosingFence(string line, char fence, int length)
        {
            int i = IndentOf(line);
            int run = 0;
            while (i + run < line.Length && line[i + run] == fence) run++;

            return run >= length && line[(i + run)..].Trim().Length == 0;
        }

        private static bool TryHeading(string line, out int level, out string text)
        {
            level = 0;
            text = "";

            int indent = IndentOf(line);
            if (indent > 3) return false;

            int i = indent;
            while (i < line.Length && line[i] == '#') i++;

            level = i - indent;
            if (level is 0 or > 6) return false;

            // «#Заголовок» без пробела — это текст, а не заголовок.
            if (i < line.Length && line[i] != ' ' && line[i] != '\t') return false;

            string body = line[i..].Trim();

            // Закрывающая группа решёток («## Заголовок ##») — оформление, но только
            // когда перед ней пробел: «# C#» заголовком с хвостом не является.
            int end = body.Length;
            while (end > 0 && body[end - 1] == '#') end--;
            if (end < body.Length && (end == 0 || body[end - 1] == ' ')) body = body[..end];

            text = body.Trim();
            return true;
        }

        private static bool IsRule(string line)
        {
            int i = IndentOf(line);
            if (i > 3 || i >= line.Length) return false;

            char marker = line[i];
            if (marker is not ('-' or '*' or '_')) return false;

            int run = 0;
            int j = i;
            while (j < line.Length && (line[j] == marker || line[j] == ' '))
            {
                if (line[j] == marker) run++;
                j++;
            }

            return run >= 3 && line[j..].Trim().Length == 0;
        }

        private static bool TryQuote(string line, out string text)
        {
            text = "";
            int i = IndentOf(line);
            if (i > 3 || i >= line.Length || line[i] != '>') return false;

            i++;
            if (i < line.Length && line[i] == ' ') i++;
            text = line[i..];
            return true;
        }

        private static bool TryListMarker(string line, out ListMarker marker)
        {
            marker = default;

            int indent = IndentOf(line);
            if (indent > 3) return false;   // глубже — это отступный код

            int i = indent;
            if (i >= line.Length) return false;

            bool ordered;

            if (line[i] is '-' or '+' or '*')
            {
                ordered = false;
                i++;
            }
            else if (line[i] >= '0' && line[i] <= '9')
            {
                int start = i;
                while (i < line.Length && line[i] >= '0' && line[i] <= '9') i++;
                if (i >= line.Length || (line[i] != '.' && line[i] != ')') || i - start > 9) return false;

                ordered = true;
                i++;   // пропускаем точку: дальше начинается содержимое пункта
            }
            else
            {
                return false;
            }

            // «- пункт» требует пробела: «-5 градусов» — это текст, а не пункт.
            if (i >= line.Length || (line[i] != ' ' && line[i] != '\t')) return false;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;

            marker = new ListMarker(indent, i, ordered);
            return true;
        }

        private static bool IsIndentedCode(string line) =>
            line.Length - line.TrimStart(' ').Length >= 4 && line.Trim().Length > 0;

        /// <summary>Строка начинает новый блок, а не продолжает абзац.</summary>
        private static bool StartsBlock(string line) =>
            TryListMarker(line, out _)
            || TryHeading(line, out _, out _)
            || IsRule(line)
            || TryQuote(line, out _)
            || TryFence(line, out _, out _, out _);

        private static bool IsBlank(string line) => line.Trim().Length == 0;

        private static int IndentOf(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        #endregion

        #region Разбор строк

        private static List<string> PrepareLines(string markdown)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(markdown)) return lines;

            string text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

            // HTML-комментарии в описаниях релизов — служебные пометки, их видеть не надо.
            while (true)
            {
                int start = text.IndexOf("<!--", StringComparison.Ordinal);
                if (start < 0) break;

                int end = text.IndexOf("-->", start, StringComparison.Ordinal);
                text = end < 0
                    ? text[..start]
                    : text[..start] + text[(end + 3)..];
            }

            foreach (string raw in text.Split('\n'))
            {
                // Табы в отступе расширяем вчетверо: иначе вложенность списка от отступа
                // в один таб была бы неотличима от абзаца.
                lines.Add(ExpandLeadingTabs(raw));
            }

            return lines;
        }

        private static string ExpandLeadingTabs(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i == 0 || line[..i].IndexOf('\t') < 0) return line;

            var expanded = new StringBuilder(line.Length + 8);
            foreach (char c in line[..i]) expanded.Append(c == '\t' ? "    " : " ");
            return expanded.Append(line[i..]).ToString();
        }

        /// <summary>
        /// Сноски вида <c>[SM-A01]: http://…</c>: собираем заранее и убираем из текста,
        /// иначе они показались бы пользователю как обычный абзац.
        /// </summary>
        private static Dictionary<string, string> CollectLinkDefinitions(List<string> lines)
        {
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith('[')) continue;

                int close = line.IndexOf(']');
                if (close < 0 || close + 1 >= line.Length || line[close + 1] != ':') continue;

                string id = line[1..close].Trim();
                string rest = line[(close + 2)..].Trim();
                if (id.Length == 0 || rest.Length == 0) continue;

                // Адрес может быть в <> и с заголовком в кавычках — берём первый кусок.
                int end = rest.IndexOf(' ');
                string url = (end < 0 ? rest : rest[..end]).Trim('<', '>');
                if (url.Length == 0) continue;

                definitions.TryAdd(id, url);
                lines[i] = "";
            }

            return definitions;
        }

        #endregion

        #region Разбор встроенной разметки

        private static List<MarkdownSpan> ParseInlines(string text, Dictionary<string, string> definitions)
        {
            var spans = new List<MarkdownSpan>();
            var plain = new StringBuilder();

            void Flush()
            {
                if (plain.Length == 0) return;
                spans.Add(new MarkdownText(plain.ToString()));
                plain.Clear();
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                switch (c)
                {
                    case '\\' when i + 1 < text.Length && IsEscapable(text[i + 1]):
                        plain.Append(text[i + 1]);
                        i += 2;
                        continue;

                    case '`':
                        if (TryCode(text, ref i, out string code))
                        {
                            Flush();
                            spans.Add(new MarkdownCodeSpan(code));
                            continue;
                        }

                        break;

                    case '!' when i + 1 < text.Length && text[i + 1] == '[':
                        // Картинку не грузим: подпись alt — это и есть смысл,
                        // а сеть в диалоге обновления приложению не нужна.
                        if (TryLink(text, ref i, image: true, definitions, out MarkdownSpan? alt))
                        {
                            Flush();
                            spans.Add(alt!);
                            continue;
                        }

                        break;

                    case '[':
                        if (TryLink(text, ref i, image: false, definitions, out MarkdownSpan? link))
                        {
                            Flush();
                            spans.Add(link!);
                            continue;
                        }

                        break;

                    case '<':
                        if (TryAutoLink(text, ref i, out MarkdownSpan? auto))
                        {
                            Flush();
                            spans.Add(auto!);
                            continue;
                        }

                        if (TryHtml(text, ref i, out MarkdownSpan? html))
                        {
                            Flush();
                            if (html != null) spans.Add(html);
                            continue;
                        }

                        break;

                    case '*' or '_':
                        if (TryEmphasis(text, ref i, out MarkdownSpan? emphasis))
                        {
                            Flush();
                            spans.Add(emphasis!);
                            continue;
                        }

                        break;

                    case '~':
                        if (TryStrike(text, ref i, out MarkdownSpan? strike))
                        {
                            Flush();
                            spans.Add(strike!);
                            continue;
                        }

                        break;

                    case '\n':
                    {
                        // Два пробела (или слэш) в конце строки — жёсткий перенос,
                        // иначе строки просто склеиваются в один абзац.
                        string tail = plain.ToString();
                        if (tail.EndsWith("  ", StringComparison.Ordinal) || tail.EndsWith('\\'))
                        {
                            plain.Length = tail.TrimEnd(' ', '\\').Length;
                            Flush();
                            spans.Add(new MarkdownLineBreak());
                        }
                        else if (spans.Count > 0 && plain.Length == 0)
                        {
                            // Перенос сразу после разметки («**жирно**\nтекст»):
                            // пробела в буфере уже нет, а склеивать нельзя.
                            spans.Add(new MarkdownLineBreak());
                        }
                        else
                        {
                            plain.Append(' ');
                        }

                        i++;
                        continue;
                    }
                }

                // Голый адрес в тексте — ссылка: в описаниях релизов их много.
                if (TryBareLink(text, ref i, out MarkdownSpan? bare))
                {
                    Flush();
                    spans.Add(bare!);
                    continue;
                }

                plain.Append(c);
                i++;
            }

            Flush();
            return spans;
        }

        private static bool TryCode(string text, ref int i, out string code)
        {
            code = "";
            int run = 0;
            while (i + run < text.Length && text[i + run] == '`') run++;

            string fence = new('`', run);
            int search = i + run;
            int close;

            while (true)
            {
                close = text.IndexOf(fence, search, StringComparison.Ordinal);
                if (close < 0) return false;

                // Более длинная серия обратных кавычек — не наш закрыватель.
                int after = close + run;
                if (after < text.Length && text[after] == '`')
                {
                    search = after + 1;
                    continue;
                }

                break;
            }

            code = text[(i + run)..close].Replace('\n', ' ');
            // Пара пробелов вокруг значения — часть синтаксиса, а не текст.
            if (code.Length > 2 && code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0)
                code = code[1..^1];

            i = close + run;
            return true;
        }

        private static bool TryEmphasis(string text, ref int i, out MarkdownSpan? span)
        {
            span = null;
            char marker = text[i];

            int run = 0;
            while (i + run < text.Length && text[i + run] == marker) run++;

            // Подчёркивание внутри слова (some_var_name) — не акцент.
            if (marker == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1])) return false;

            int search = i + run;
            int close;
            while (true)
            {
                close = IndexOfRun(text, search, marker, run);
                if (close < 0) return false;

                if (marker == '_' && close + run < text.Length && char.IsLetterOrDigit(text[close + run]))
                {
                    search = close + run;
                    continue;
                }

                break;
            }

            string inner = text[(i + run)..close];
            if (inner.Trim().Length == 0) return false;

            bool bold = run >= 2;
            span = new MarkdownEmphasis(bold, !bold, false,
                ParseInlines(inner, EmptyDefinitions));
            i = close + run;
            return true;
        }

        private static bool TryStrike(string text, ref int i, out MarkdownSpan? span)
        {
            span = null;
            if (i + 1 >= text.Length || text[i + 1] != '~') return false;

            int close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
            if (close < 0) return false;

            string inner = text[(i + 2)..close];
            if (inner.Trim().Length == 0) return false;

            span = new MarkdownEmphasis(false, false, true, ParseInlines(inner, EmptyDefinitions));
            i = close + 2;
            return true;
        }

        private static bool TryLink(string text, ref int i, bool image, Dictionary<string, string> definitions,
            out MarkdownSpan? span)
        {
            span = null;

            // У картинки на позиции i стоит «!», а открывающая скобка — на i + 1.
            int labelOpen = image ? i + 1 : i;

            int labelEnd = FindClosing(text, labelOpen, '[', ']');
            if (labelEnd < 0) return false;

            string label = text[(labelOpen + 1)..labelEnd];

            if (labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
            {
                int parenEnd = FindClosing(text, labelEnd + 1, '(', ')');
                if (parenEnd < 0) return false;

                string destination = text[(labelEnd + 2)..parenEnd].Trim();
                int space = destination.IndexOf(' ');
                if (space >= 0) destination = destination[..space];
                destination = destination.Trim('<', '>');

                if (destination.Length == 0) return false;

                span = image
                    ? new MarkdownText(ImageText(label, destination))
                    : new MarkdownLink(ParseInlines(label, definitions), destination);
                i = parenEnd + 1;
                return true;
            }

            // Ссылка по сноске: [текст][id] или [текст][] с определением выше по тексту.
            if (labelEnd + 1 < text.Length && text[labelEnd + 1] == '[')
            {
                int referenceEnd = FindClosing(text, labelEnd + 1, '[', ']');
                string id = referenceEnd < 0 ? "" : text[(labelEnd + 2)..referenceEnd];
                if (id.Length == 0) id = label;

                if (definitions.TryGetValue(id.Trim(), out string? url))
                {
                    span = image
                        ? new MarkdownText(ImageText(label, url))
                        : new MarkdownLink(ParseInlines(label, definitions), url);
                    i = referenceEnd < 0 ? labelEnd + 1 : referenceEnd + 1;
                    return true;
                }
            }

            return false;
        }

        private static string ImageText(string label, string url)
        {
            string alt = PlainText(ParseInlines(label, EmptyDefinitions)).Trim();
            return alt.Length > 0 ? alt : url;
        }

        private static bool TryAutoLink(string text, ref int i, out MarkdownSpan? span)
        {
            span = null;
            int end = text.IndexOf('>', i);
            if (end < 0) return false;

            string inside = text[(i + 1)..end];
            if (!LooksLikeUrl(inside)) return false;

            span = new MarkdownLink([new MarkdownText(inside)], inside);
            i = end + 1;
            return true;
        }

        private static bool TryBareLink(string text, ref int i, out MarkdownSpan? span)
        {
            span = null;

            bool starts = Matches(text, i, "https://")
                         || Matches(text, i, "http://")
                         || Matches(text, i, "www.");
            if (!starts) return false;

            // Адрес внутри уже готовой ссылки не трогаем.
            int previous = i - 1;
            if (previous >= 0 && (char.IsLetterOrDigit(text[previous]) || text[previous] == '/')) return false;

            int end = i;
            while (end < text.Length && !char.IsWhiteSpace(text[end])
                   && text[end] is not ('(' or ')' or '[' or ']' or '<' or '>'))
                end++;

            // Знаки препинания в конце предложения — часть текста, а не адреса.
            while (end > i && ".,;:!?'\"".Contains(text[end - 1])) end--;

            string url = text[i..end];
            if (url.Length == 0) return false;

            string href = url.StartsWith("www.", StringComparison.Ordinal) ? "https://" + url : url;
            span = new MarkdownLink([new MarkdownText(url)], href);
            i = end;
            return true;
        }

        /// <summary>
        /// HTML в описании релиза — обычное дело (бейджи, `&lt;br&gt;`, `&lt;details&gt;`).
        /// Разбирать его нечем, поэтому теги убираются, а `&lt;br&gt;` становится переносом.
        /// </summary>
        private static bool TryHtml(string text, ref int i, out MarkdownSpan? span)
        {
            span = null;
            if (i + 1 >= text.Length || text[i] != '<') return false;

            int end = text.IndexOf('>', i);
            if (end < 0) return false;

            string tag = text[(i + 1)..end];

            // «a < b > c» — это текст, а не тег: без такой проверки знак сравнения
            // съел бы кусок предложения вместе с ближайшим «>».
            if (!IsTagLike(tag)) return false;

            string name = new string(tag.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();

            if (name is "br" or "p")
            {
                span = new MarkdownLineBreak();
            }
            else if (name == "img")
            {
                string alt = Attribute(tag, "alt");
                string src = Attribute(tag, "src");
                if (alt.Length > 0) span = new MarkdownText(alt);
                else if (src.Length > 0) span = new MarkdownLink([new MarkdownText(src)], src);
            }

            i = end + 1;
            return true;
        }

        private static bool IsTagLike(string tag)
        {
            if (tag.Length == 0) return false;

            int i = 0;
            if (tag[i] is '/' or '!') i++;
            if (i >= tag.Length || !char.IsLetter(tag[i])) return false;
            while (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] is '-' or '_')) i++;

            // Дальше — только атрибуты: без «<» внутри и без символов, которых в теге
            // не бывает. Всё остальное означает, что это обычный текст.
            for (; i < tag.Length; i++)
            {
                char c = tag[i];
                if (c is '<' or '>') return false;
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)) continue;
                if (c is '=' or '"' or '\'' or '/' or ':' or '.' or '#' or '?' or '!') continue;
                return false;
            }

            return true;
        }

        private static string Attribute(string tag, string name)
        {
            string quoted = $"{name}=\"";
            int start = tag.IndexOf(quoted, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                quoted = $"{name}='";
                start = tag.IndexOf(quoted, StringComparison.OrdinalIgnoreCase);
            }

            if (start < 0) return "";

            start += quoted.Length;
            int end = tag.IndexOf(quoted[0], start);
            return end < 0 ? "" : tag[start..end];
        }

        private static int IndexOfRun(string text, int from, char marker, int run)
        {
            for (int i = from; i < text.Length; i++)
            {
                if (text[i] != marker) continue;

                int length = 0;
                while (i + length < text.Length && text[i + length] == marker) length++;
                if (length >= run) return i;
                i += length - 1;
            }

            return -1;
        }

        /// <summary>
        /// Индекс закрывающей пары. Считает от <paramref name="open"/> включительно:
        /// открывающая скобка уже на этом месте, иначе глубина никогда не сошлась бы
        /// к нулю и ссылка не нашла бы свой хвост.
        /// </summary>
        private static int FindClosing(string text, int open, char openChar, char closeChar)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == openChar) depth++;
                else if (text[i] == closeChar && --depth == 0) return i;
            }

            return -1;
        }

        private static bool LooksLikeUrl(string value) =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

        private static bool Matches(string text, int i, string prefix) =>
            i + prefix.Length <= text.Length
            && text.AsSpan(i, prefix.Length).SequenceEqual(prefix);

        private static bool IsEscapable(char c) =>
            c is not ('\n' or '\r') && !char.IsLetterOrDigit(c);

        private static readonly Dictionary<string, string> EmptyDefinitions = new(StringComparer.OrdinalIgnoreCase);

        #endregion
    }
}