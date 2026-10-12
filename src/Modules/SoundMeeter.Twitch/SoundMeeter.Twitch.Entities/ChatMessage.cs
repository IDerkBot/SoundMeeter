using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Twitch.Entities;

/// <summary>Сообщение чата: логин автора без «@» и текст реплики.</summary>
public record ChatMessage(string User, string Text);
