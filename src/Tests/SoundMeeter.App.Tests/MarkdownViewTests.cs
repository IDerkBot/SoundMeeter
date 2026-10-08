using SoundMeeter.Controls;
using SoundMeeter.Tests.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Показ описания релиза в окне обновления: разметка должна превратиться в
/// оформленный документ, а не остаться исходником.
///
/// Проверяется ровно то, что видит пользователь: заголовок стал крупнее и
/// полужирнее, ссылка стала ссылкой и открывается, а адрес из чужого описания
/// релиза не открывается вообще. Браузер в тесте не запускается — адрес
/// перехватывается через <see cref="MarkdownView.UrlOpener"/>.
/// </summary>
public class MarkdownViewTests
{
    [Fact]
    public void HeadingBecomesALargerBoldParagraph()
    {
        UiHost.Run(() =>
        {
            var view = new MarkdownView { Markdown = "## Исправления\n\n- Поправлен выбор логов." };
            VisualTree.Layout(view, 480, 400);

            var document = view.Document!;
            Assert.Equal(2, document.Blocks.Count);

            var heading = Assert.IsType<Paragraph>(document.Blocks.First());
            Assert.Equal(14, heading.FontSize);
            Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
            Assert.Equal("Исправления", new TextRange(heading.ContentStart, heading.ContentEnd).Text);

            // Пункт списка: маркер отделён от текста, иначе получилось бы «•Поправлен».
            var item = Assert.IsType<Paragraph>(document.Blocks.Last());
            string text = new TextRange(item.ContentStart, item.ContentEnd).Text;
            Assert.StartsWith("• ", text);
            Assert.Contains("Поправлен выбор логов.", text);
        });
    }

    [Fact]
    public void CodeBlockIsShownMonospaced()
    {
        UiHost.Run(() =>
        {
            var view = new MarkdownView { Markdown = "```\nGet-Process | **не** markdown\n```" };
            VisualTree.Layout(view, 480, 400);

            var code = Assert.IsType<Paragraph>(Assert.Single(view.Document!.Blocks));

            // Разметка внутри блока кода остаётся текстом — иначе лог разбора
            // показался бы с полужирным «не».
            Assert.Equal("Get-Process | **не** markdown", new TextRange(code.ContentStart, code.ContentEnd).Text);
            Assert.Equal("Consolas, Courier New, monospace", code.FontFamily.Source);
        });
    }

    [Fact]
    public void LinkIsClickableAndGoesToTheBrowser()
    {
        Uri? opened = null;
        MarkdownView.UrlOpener = uri => opened = uri;

        try
        {
            UiHost.Run(() =>
            {
                var view = new MarkdownView { Markdown = "Подробности: [в репозитории](https://github.com/a/b)" };
                VisualTree.Layout(view, 480, 400);

                var hyperlink = Assert.Single(AllHyperlinks(view));
                Assert.Equal("https://github.com/a/b", hyperlink.NavigateUri!.ToString());

                // Событие навигации поднимаем на самой ссылке: так же, как это делает
                // щелчок мыши, и ответ обработчика виден без настоящего браузера.
                hyperlink.RaiseEvent(new RequestNavigateEventArgs(hyperlink.NavigateUri, null));
            });

            Assert.Equal("https://github.com/a/b", opened?.ToString());
        }
        finally
        {
            // Статическое состояние на весь процесс: без возврата следующий тест
            // в этом же процессе писал бы в чужой обработчик.
            MarkdownView.UrlOpener = null;
        }
    }

    [Fact]
    public void LinkToAnUnsupportedSchemeStaysPlainText()
    {
        UiHost.Run(() =>
        {
            var view = new MarkdownView { Markdown = "[нажми](javascript:alert(1))" };
            VisualTree.Layout(view, 480, 400);

            // Описание релиза приходит извне, а javascript:-адрес в обработчике
            // навигации — это выполнение чужого кода. Поэтому остаётся текст.
            Assert.Empty(AllHyperlinks(view));

            var paragraph = Assert.IsType<Paragraph>(Assert.Single(view.Document!.Blocks));
            Assert.Equal("нажми", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
        });
    }

    [Fact]
    public void EmptyNotesStillGiveADocument()
    {
        UiHost.Run(() =>
        {
            var view = new MarkdownView { Markdown = "" };
            VisualTree.Layout(view, 480, 400);

            // RichTextBox без содержимого нечем рисовать, а «описания нет» уже
            // показывает ViewModel — пустой документ здесь просто не роняет окно.
            Assert.NotNull(view.Document);
            Assert.Single(view.Document!.Blocks);
        });
    }

    private static List<Hyperlink> AllHyperlinks(DependencyObject root)
    {
        var found = new List<Hyperlink>();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Hyperlink link) found.Add(link);
            found.AddRange(AllHyperlinks(child));
        }

        // Ссылка может остаться и вне визуального дерева (документ ещё не размечен):
        // тогда ищем её прямо в блоках.
        if (found.Count == 0 && root is RichTextBox owner)
            foreach (var paragraph in owner.Document?.Blocks.OfType<Paragraph>() ?? [])
                Collect(paragraph.Inlines, found);

        return found;

        static void Collect(InlineCollection inlines, List<Hyperlink> into)
        {
            foreach (var inline in inlines)
            {
                if (inline is Hyperlink link) into.Add(link);
                if (inline is Span span) Collect(span.Inlines, into);
            }
        }
    }
}