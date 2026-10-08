using SoundMeeter.Controls;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Разбор markdown описания релиза.
///
/// Проверяется не «формат вообще», а то, чем заполнены реальные описания
/// (заголовки разделов, списки пунктов, ссылки) плюс то, что ломается чаще всего:
/// вложенность списков, разметка внутри ссылки, html-бейджи и подчёркивание внутри
/// слова. Разметка приходит с GitHub, и сломанный разбор здесь выглядит как «в
/// changelog мусор», а не как ошибка.
/// </summary>
public class MarkdownParserTests
{
    private static IReadOnlyList<MarkdownBlock> Parse(string markdown) => MarkdownParser.Parse(markdown);

    private static MarkdownList List(IReadOnlyList<MarkdownBlock> blocks) =>
        Assert.IsType<MarkdownList>(Assert.Single(blocks));

    private static string TextOf(IReadOnlyList<MarkdownSpan> spans) => MarkdownParser.PlainText(spans);

    [Fact]
    public void HeadingsAndListsAreSeparateBlocks()
    {
        // Ровно то, чем заполнено описание релиза 0.1.1: раздел и его пункты.
        var blocks = Parse("## Исправления\r\n- Поправлен выбор логов.\r\n- Поправлена фильтрация.\r\n"
                         + "\r\n## Улучшения\r\n- Добавлен эквалайзер.");

        Assert.Equal(4, blocks.Count);
        Assert.Equal(2, Assert.IsType<MarkdownHeading>(blocks[0]).Level);
        Assert.Equal("Исправления", TextOf(Assert.IsType<MarkdownHeading>(blocks[0]).Spans));
        Assert.Equal(2, List([blocks[1]]).Items.Count);
        Assert.Equal("Добавлен эквалайзер.", TextOf(List([blocks[3]]).Items[0].Spans));
    }

    [Fact]
    public void HeadingKeepsItsLevelAndDropsTheClosingHashes()
    {
        var heading = Assert.IsType<MarkdownHeading>(Assert.Single(Parse("### Заголовок ###")));
        Assert.Equal(3, heading.Level);
        Assert.Equal("Заголовок", TextOf(heading.Spans));
    }

    [Fact]
    public void SevenHashesIsNotAHeading()
    {
        // CommonMark держит шесть уровней; лишняя решётка — это текст.
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("####### семь")));
        Assert.Equal("####### семь", TextOf(paragraph.Spans));
    }

    [Fact]
    public void HashWithoutSpaceIsNotAHeading()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("#Незаголовок")));
        Assert.Equal("#Незаголовок", TextOf(paragraph.Spans));
    }

    [Fact]
    public void EmphasisInlineCodeAndLinkAreParsed()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(
            Parse("Исправлено **отображение** и *фильтрация* в `LogWindow`, см. [релиз](https://github.com/x/y).")));

        Assert.Contains(paragraph.Spans, s => s is MarkdownEmphasis { Bold: true });
        Assert.Contains(paragraph.Spans, s => s is MarkdownEmphasis { Italic: true });

        var code = Assert.IsType<MarkdownCodeSpan>(Assert.Single(paragraph.Spans.OfType<MarkdownCodeSpan>()));
        Assert.Equal("LogWindow", code.Text);

        var link = Assert.Single(paragraph.Spans.OfType<MarkdownLink>());
        Assert.Equal("релиз", TextOf(link.Spans));
        Assert.Equal("https://github.com/x/y", link.Url);
    }

    [Fact]
    public void UnderscoreInsideAWordIsNotEmphasis()
    {
        // some_var_name — имя переменной, а не «подчёркнутое слово».
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("Правка some_var_name в коде")));
        Assert.Equal("Правка some_var_name в коде", TextOf(paragraph.Spans));
        Assert.DoesNotContain(paragraph.Spans, s => s is MarkdownEmphasis);
    }

    [Fact]
    public void EmphasisInsideALinkIsParsed()
    {
        var link = Assert.IsType<MarkdownLink>(Assert.Single(
            Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("[**жирная** ссылка](https://x)"))).Spans));

        Assert.Equal("жирная ссылка", TextOf(link.Spans));
        Assert.Contains(link.Spans, s => s is MarkdownEmphasis { Bold: true });
    }

    [Fact]
    public void BareUrlBecomesALink()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("Отчёт: https://example.com/a?b=1, всё.")));
        var link = Assert.Single(paragraph.Spans.OfType<MarkdownLink>());

        Assert.Equal("https://example.com/a?b=1", link.Url);
        Assert.Equal("Отчёт: https://example.com/a?b=1, всё.", TextOf(paragraph.Spans));
    }

    [Fact]
    public void ReferenceLinkUsesItsDefinition()
    {
        var blocks = Parse("[примечание][sm] и [другое][].\n\n[sm]: https://example.com/sm\n[другое]: https://example.com/d\n");
        var paragraph = Assert.IsType<MarkdownParagraph>(blocks[0]);

        Assert.Equal(2, paragraph.Spans.OfType<MarkdownLink>().Count());

        // Определение не показывается как абзац — иначе в описании релиза была бы
        // строка вида «[sm]: https://…».
        Assert.Single(blocks);
    }

    [Fact]
    public void OrderedListIsNumbered()
    {
        var list = List(Parse("1. Первый шаг.\n2. Второй шаг."));

        Assert.True(list.Ordered);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(1, list.Items[0].Number);
        Assert.Equal(2, list.Items[1].Number);
        Assert.Equal("Второй шаг.", TextOf(list.Items[1].Spans));
    }

    [Fact]
    public void NestedListGoesIntoTheParentItem()
    {
        var list = List(Parse("- Верхний пункт.\n  - вложенный\n  - ещё один\n- Второй верхний."));

        Assert.Equal(2, list.Items.Count);
        var nested = Assert.Single(list.Items[0].Children);
        Assert.Equal(2, List([nested]).Items.Count);
        Assert.Empty(list.Items[1].Children);
    }

    [Fact]
    public void TaskListMarkerMovesOutOfTheText()
    {
        var list = List(Parse("- [x] сделано\n- [ ] не сделано"));

        Assert.True(list.Items[0].CheckedState);
        Assert.False(list.Items[1].CheckedState);
        Assert.Equal("сделано", TextOf(list.Items[0].Spans));
        Assert.Equal("не сделано", TextOf(list.Items[1].Spans));
    }

    [Fact]
    public void PlainDashWithoutSpaceIsNotAListItem()
    {
        // «-5 градусов» — это текст, а не пункт списка.
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("Охлаждение на -5 градусов")));
        Assert.Equal("Охлаждение на -5 градусов", TextOf(paragraph.Spans));
    }

    [Fact]
    public void LazyContinuationStaysInsideTheItem()
    {
        var list = List(Parse("- пункт,\n  продолжающийся на следующей строке"));

        Assert.Single(list.Items);
        Assert.Equal("пункт, продолжающийся на следующей строке", TextOf(list.Items[0].Spans));
    }

    [Fact]
    public void FencedCodeIsKeptVerbatim()
    {
        var code = Assert.IsType<MarkdownCode>(Assert.Single(Parse("```powershell\nGet-Process | **не** markdown\n```")));

        Assert.Equal("powershell", code.Language);
        Assert.Equal("Get-Process | **не** markdown", code.Code);
    }

    [Fact]
    public void QuoteKeepsItsInnerBlocks()
    {
        var quote = Assert.IsType<MarkdownQuote>(Assert.Single(Parse("> Внимание:\n> - пункт\n> - второй")));

        // Абзац и список внутри цитаты остаются отдельными блоками — их видно
        // пользователю как цитату с двумя формами.
        Assert.Equal(2, quote.Blocks.Count);
        Assert.Equal(2, List([quote.Blocks[1]]).Items.Count);
    }

    [Fact]
    public void HorizontalRuleIsItsOwnBlock()
    {
        var blocks = Parse("сверху\n\n---\n\nснизу");

        Assert.Equal(3, blocks.Count);
        Assert.IsType<MarkdownRule>(blocks[1]);
    }

    [Fact]
    public void HtmlTagsAreStrippedButBrBecomesALineBreak()
    {
        var blocks = Parse("Строка<br>вторая <b>жирная</b> <details>подробности</details>");
        var paragraph = Assert.IsType<MarkdownParagraph>(blocks[0]);

        Assert.Equal("Строка вторая жирная подробности", TextOf(paragraph.Spans));
        Assert.Contains(paragraph.Spans, s => s is MarkdownLineBreak);
    }

    [Fact]
    public void ImageBecomesItsAltText()
    {
        // Бейджи в описаниях релизов — обычное дело, но грузить из них картинки
        // в диалог обновления незачем: остаётся подпись.
        var paragraph = Assert.IsType<MarkdownParagraph>(
            Assert.Single(Parse("![Сборка](https://ci.example/badge.svg) passed")));

        Assert.Equal("Сборка passed", TextOf(paragraph.Spans));
        Assert.DoesNotContain(paragraph.Spans, s => s is MarkdownLink);
    }

    [Fact]
    public void HtmlCommentIsRemoved()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("до <!-- заметка для автора --> после")));
        Assert.Equal("до  после", TextOf(paragraph.Spans));
    }

    [Fact]
    public void ComparisonSignIsNotSwallowedAsATag()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("a < b и c > d")));
        Assert.Equal("a < b и c > d", TextOf(paragraph.Spans));
    }

    [Fact]
    public void EscapesAndHardBreaksSurvive()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(Parse("литерал \\*звёзды\\*  \nвторая строка")));

        Assert.Equal("литерал *звёзды* вторая строка", TextOf(paragraph.Spans));
        Assert.Contains(paragraph.Spans, s => s is MarkdownLineBreak);
    }

    [Fact]
    public void IndentedCodeBlockKeepsItsText()
    {
        var blocks = Parse("обычный абзац\n\n    двоеточие: значение");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("двоеточие: значение", Assert.IsType<MarkdownCode>(blocks[1]).Code);
    }

    [Fact]
    public void EmptyInputProducesNothing()
    {
        Assert.Empty(MarkdownParser.Parse(""));
        Assert.Empty(MarkdownParser.Parse("   \r\n\r\n  "));
    }
}