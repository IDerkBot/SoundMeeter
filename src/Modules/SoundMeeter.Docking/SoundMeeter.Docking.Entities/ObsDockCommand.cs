namespace SoundMeeter.Docking.Entities
{
    /// <summary>Команда от дока к приложению: {op, id, kind, v}.</summary>
    public sealed class ObsDockCommand
    {
        public string Op { get; init; } = "";
        public string Id { get; init; } = "";
        public string Kind { get; init; } = ObsDockChannels.Input;

        /// <summary>Числовое значение: дБ для <see cref="ObsDockOps.Volume"/>, 1/0 — для флагов.</summary>
        public float Value { get; set; }

        /// <summary>Значение флага (mute/solo/mono) — извлекается из Value.</summary>
        public bool Flag { get; set; }

        /// <summary>Разбор команды из JSON. null — команда не распознана.</summary>
        public static ObsDockCommand? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

                string op = Str(root, "op") ?? "";
                string id = Str(root, "id") ?? "";
                if (op.Length == 0 || id.Length == 0) return null;

                string kind = Str(root, "kind") ?? ObsDockChannels.Input;
                if (kind != ObsDockChannels.Input && kind != ObsDockChannels.Output)
                    kind = ObsDockChannels.Input;

                var command = new ObsDockCommand { Op = op, Id = id, Kind = kind };
                if (root.TryGetProperty("v", out var v))
                {
                    if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetSingle(out var num))
                    {
                        command.Value = num;
                        command.Flag = Math.Abs(num) > float.Epsilon;
                    }
                    else if (v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    {
                        command.Flag = v.GetBoolean();
                        command.Value = command.Flag ? 1f : 0f;
                    }
                }

                return command;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static string? Str(System.Text.Json.JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
