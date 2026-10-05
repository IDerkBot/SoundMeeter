using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace SoundMeeter.Controls
{
    /// <summary>
    /// Показ текста в разметке markdown. Используется для описания релиза в окне
    /// обновления: GitHub отдаёт его как markdown, и обычным текстом он выглядел
    /// как дамп разметки — с <c>##</c>, <c>-*</c> и склеенными строками.
    ///
    /// Это <see cref="RichTextBox"/> только для отображения: документ строится из
    /// <see cref="MarkdownParser"/> и меняется целиком при смене текста. Отдельный
    /// <c>ScrollViewer</c> не нужен — своя прокрутка у RichTextBox есть, а два
    /// скроллбара в одном окне выглядели бы ошибкой.
    ///
    /// Ссылки открываются в браузере пользователя, но только http/https/mailto:
    /// описание релиза приходит извне, а <c>javascript:</c> в адресной строке WPF —
    /// это выполнение чужого кода.
    /// </summary>
    public sealed class MarkdownView : RichTextBox
    {
        // Палитра повторяет остальной диалог: тема тёмная, акцент один.
        private static readonly Brush Body = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        private static readonly Brush Heading = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xE0));
        private static readonly Brush CodeText = new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x7D));
        private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1E));
        private static readonly Brush QuoteBar = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x50));

        private static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

        /// <summary>Куда открывать ссылки. Не задано — системный браузер.</summary>
        public static Action<Uri>? UrlOpener { get; set; }

        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.Register(
                nameof(Markdown),
                typeof(string),
                typeof(MarkdownView),
                new PropertyMetadata("", OnMarkdownChanged));

        public MarkdownView()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsTabStop = false;
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Foreground = Body;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));

            // Документ собирается сразу: значение по умолчанию у Markdown не
            // меняет свойство, и без этого первый кадр показал бы пустой RichTextBox.
            Render("");
        }

        /// <summary>Исходный markdown: в документ попадает только разобранный вид.</summary>
        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is MarkdownView view) view.Render(e.NewValue as string ?? "");
        }

        private FlowDocument NewDocument() => new()
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = Body,
            PagePadding = new Thickness(0, 0, 6, 0)
        };

        private void Render(string markdown)
        {
            var document = NewDocument();

            foreach (var block in MarkdownParser.Parse(markdown))
                Append(document, block, level: 0, quoteDepth: 0);

            // Пустой документ вместо пустого окна: у RichTextBox без содержимого
            // нечем рисовать, а «описания нет» показывает уже ViewModel.
            if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());

            Document = document;
        }

        /// <summary>
        /// <paramref name="quoteDepth"/> — вложенность цитаты: её полоса и приглушённый
        /// текст повторяются на каждом абзаце, поэтому цитата разбирается в тот же
        /// документ, а не во вложенную структуру, которой потом негде рисоваться.
        /// </summary>
        private static void Append(FlowDocument target, MarkdownBlock block, int level, int quoteDepth)
        {
            switch (block)
            {
                case MarkdownParagraph paragraph:
                    target.Blocks.Add(Block(paragraph.Spans, spacing: 6, quoteDepth));
                    break;

                case MarkdownHeading heading:
                    target.Blocks.Add(Header(heading, quoteDepth));
                    break;

                case MarkdownCode code:
                    target.Blocks.Add(Code(code, quoteDepth));
                    break;

                case MarkdownQuote quote:
                    foreach (var inner in quote.Blocks)
                        Append(target, inner, level, quoteDepth + 1);
                    break;

                case MarkdownRule:
                    target.Blocks.Add(Rule(quoteDepth));
                    break;

                case MarkdownList list:
                    foreach (var item in list.Items)
                        Item(target, item, list.Ordered, level, quoteDepth);
                    break;
            }
        }

        private static Paragraph Block(IReadOnlyList<MarkdownSpan> spans, double spacing, int quoteDepth)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, spacing, 0, 0), Foreground = Body };
            Add(paragraph.Inlines, spans);
            Quote(paragraph, quoteDepth);
            return paragraph;
        }

        private static Paragraph Header(MarkdownHeading heading, int quoteDepth)
        {
            var paragraph = Block(heading.Spans, spacing: heading.Level <= 2 ? 10 : 6, quoteDepth);
            paragraph.FontSize = heading.Level switch { 1 => 16, 2 => 14, 3 => 13, _ => 12 };
            paragraph.Foreground = quoteDepth > 0 ? Muted : Heading;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Margin = new Thickness(paragraph.Margin.Left, paragraph.Margin.Top, 0, 2);
            return paragraph;
        }

        private static Paragraph Code(MarkdownCode code, int quoteDepth)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(8, 4, 8, 4),
                Background = CodeBackground,
                BorderBrush = QuoteBar,
                BorderThickness = new Thickness(1, 0, 0, 0)
            };

            paragraph.SetValue(TextElement.FontFamilyProperty, Mono);
            paragraph.SetValue(TextElement.FontSizeProperty, 11.5);

            string[] lines = code.Code.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.Add(new Run(lines[i]));
            }

            Quote(paragraph, quoteDepth);
            return paragraph;
        }

        private static Paragraph Rule(int quoteDepth)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 8, 0, 8),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = QuoteBar
            };

            Quote(paragraph, quoteDepth);
            return paragraph;
        }

        private static void Item(FlowDocument target, MarkdownListItem item, bool ordered, int level, int quoteDepth)
        {
            var paragraph = new Paragraph { Foreground = Body };

            // Маркер выступает влево от текста (висячий отступ), иначе вторая строка
            // длинного пункта начиналась бы под самой буллетой. Вложенность — шагом
            // по уровню, тот же, что и в разборе.
            paragraph.Margin = new Thickness(level * 14 + 16, 0, 0, 2);
            paragraph.TextIndent = -16;

            string marker = item.CheckedState switch
            {
                true => "☑ ",
                false => "☐ ",
                _ when !ordered => "• ",
                _ => $"{item.Number}. "
            };

            paragraph.Inlines.Add(new Run(marker) { Foreground = Accent });
            Add(paragraph.Inlines, item.Spans);
            Quote(paragraph, quoteDepth);

            target.Blocks.Add(paragraph);

            foreach (var child in item.Children)
                Append(target, child, level + 1, quoteDepth);
        }

        /// <summary>Цитата — вертикальная черта слева и приглушённый текст, на каждом абзаце.</summary>
        private static void Quote(Paragraph paragraph, int depth)
        {
            if (depth <= 0) return;

            // Свою границу не затираем: линия `---` внутри цитаты остаётся линией,
            // а не превращается в ещё одну вертикальную черту.
            if (paragraph.BorderThickness.Left == 0)
            {
                paragraph.BorderThickness = new Thickness(3, paragraph.BorderThickness.Top,
                    paragraph.BorderThickness.Right, paragraph.BorderThickness.Bottom);
                paragraph.BorderBrush = QuoteBar;
            }

            paragraph.Padding = new Thickness(8, 0, 0, 0);
            paragraph.Foreground = Muted;
        }

        private static void Add(InlineCollection inlines, IReadOnlyList<MarkdownSpan> spans)
        {
            foreach (var span in spans)
                if (Create(span) is { } inline) inlines.Add(inline);
        }

        private static Inline? Create(MarkdownSpan span)
        {
            switch (span)
            {
                case MarkdownText plain:
                    return new Run(plain.Text);

                case MarkdownCodeSpan code:
                    return new Run(code.Text) { Foreground = CodeText, FontFamily = Mono };

                case MarkdownEmphasis emphasis:
                    var styled = new Span();
                    if (emphasis.Bold) styled.FontWeight = FontWeights.Bold;
                    if (emphasis.Italic) styled.FontStyle = FontStyles.Italic;
                    if (emphasis.Strike) styled.TextDecorations = TextDecorations.Strikethrough;
                    Add(styled.Inlines, emphasis.Spans);
                    return styled;

                case MarkdownLink link:
                    // Ссылка без безопасного адреса остаётся текстом: иначе клик по
                    // чужому `javascript:` ушёл бы в обработчик навигации WPF.
                    if (SafeUri(link.Url) is not { } uri) return Create(LinkText(link));

                    // Оформление задаётся самой ссылкой: её свойства наследуются
                    // вложенными Span'ами, поэтому жирный текст внутри ссылки тоже
                    // остаётся ссылкой, а не чёрным по ссылке.
                    var hyperlink = new Hyperlink
                    {
                        NavigateUri = uri,
                        Foreground = Accent,
                        TextDecorations = TextDecorations.Underline
                    };
                    Add(hyperlink.Inlines, link.Spans);

                    return hyperlink;

                case MarkdownLineBreak:
                    return new LineBreak();

                default:
                    return null;
            }
        }

        /// <summary>Подпись ссылки, если открывать её нечем.</summary>
        private static MarkdownSpan LinkText(MarkdownLink link) =>
            link.Spans.Count == 1 && link.Spans[0] is MarkdownText text
                ? text
                : new MarkdownText(MarkdownParser.PlainText(link.Spans));

        private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            if (e.Source is not Hyperlink { NavigateUri: { } uri }) return;

            try
            {
                if (UrlOpener is { } open) open(uri);
                else Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                          or UriFormatException or NotSupportedException)
            {
                // Браузера нет или адрес не открылся — это не причина ронять окно.
            }
        }

        /// <summary>
        /// Адрес для открытия: только http, https и mailto. Всё остальное, включая
        /// <c>javascript:</c> и <c>file:</c>, — null.
        /// </summary>
        private static Uri? SafeUri(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto"
                ? uri
                : null;
    }
}