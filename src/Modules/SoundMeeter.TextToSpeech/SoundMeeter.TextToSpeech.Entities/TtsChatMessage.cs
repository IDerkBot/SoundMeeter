using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.TextToSpeech.Entities;

/// <summary>Сообщение чата: логин автора без «@» и текст реплики.</summary>
public sealed record TtsChatMessage(string User, string Text);
