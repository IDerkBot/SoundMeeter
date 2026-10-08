using SoundMeeter.Models;
using System.Globalization;
using System.Text;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Сборка описания цепочки релизов в один markdown.
    ///
    /// Обновление через несколько версий (с 0.0.1 сразу на 0.0.7) показывало только
    /// записи последнего релиза, и пользователь не знал, что именно он получит.
    /// Теперь окно показывает все пропущенные версии, а разбирает их тот же
    /// MarkdownView, что и описание одного релиза.
    ///
    /// Собирать текстом, а не списком объектов для UI, потому что показывать надо
    /// один прокручиваемый документ: у каждого релиза свой заголовок верхнего
    /// уровня, а его разделы остаются вложенными (`##`) — иерархия получается из
    /// самого markdown, без ручной вёрстки вложенности.
    /// </summary>
    public static class UpdateChangelog
    {
        /// <summary>
        /// Описание по цепочке релизов: от новых к старым, у каждого свой заголовок
        /// «версия (дата)» и заметки релиза. У релиза без заметок подставляется
        /// <paramref name="emptyNotes"/> — пустого заголовка пользователь не должен
        /// видеть, а «описания нет» уже есть в словаре строк.
        /// </summary>
        public static string Compose(IEnumerable<UpdateRelease> releases, string emptyNotes)
        {
            ArgumentNullException.ThrowIfNull(releases);

            var text = new StringBuilder();
            foreach (var release in releases)
            {
                string notes = string.IsNullOrWhiteSpace(release.ReleaseNotes)
                    ? emptyNotes
                    : release.ReleaseNotes.Trim();

                if (text.Length > 0) text.Append("\n\n");

                // Дата в формате ISO: её не нужно локализовать, а «2026-10-04» не
                // спорит с привычными «4 октября» и одинаково читается в обоих языках.
                text.Append("# ").Append(release.Version);
                if (release.PublishedAt is { } published)
                {
                    text.Append(" (")
                        .Append(published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                        .Append(')');
                }

                text.Append("\n\n").Append(notes);
            }

            return text.ToString();
        }
    }
}