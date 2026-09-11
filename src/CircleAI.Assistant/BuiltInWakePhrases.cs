// BuiltInWakePhrases.cs
//
// The wake phrases that ship with the app, by language.
//
// FIVE OF SEVENTY-FIVE. The app speaks seventy-five languages and arrives
// knowing how to be woken in five of them, because a wake phrase is not a
// translation - it is a name with an honorific around it, chosen so the model
// can actually hear it, and each one had to be measured.
//
// This is a plain table rather than a call into the engine because it is read by
// the shared UI, which is loaded by a browser as well as by a phone and
// references nothing. WakeLanguagesTests asserts it agrees with
// WakePhraseBook.CandidatesByLanguage in CircleAI.Voice; a hand-kept copy of
// somebody else's table is otherwise a lie waiting for the next commit.
//
// WHAT IT IS NOT: a picker. There was a select on the settings screen filled
// from this idea - choose the language of your wake phrase - and it let somebody
// run the app in English and wake it with ビーさん. The language comes from
// AppSettings.Language now. This table only answers "does that language arrive
// with a phrase, and what is it", and when the answer is no the screen offers to
// let the owner add one rather than quietly listening in English.

namespace CircleAI.Samples.It;

/// <summary>What the app knows how to be woken with, before anybody adds theirs.</summary>
public static class BuiltInWakePhrases
{
    /// <summary>
    /// The phrases that ship, by language, best first.
    /// </summary>
    /// <remarks>
    /// SEVERAL PER LANGUAGE ON PURPOSE. Japanese has ビーさん, ビーさま and Bee san
    /// - the same name with different honorifics, plus a romanised form for
    /// somebody whose keyboard is not Japanese - and which of them a person
    /// actually says is theirs to pick, not ours to guess. The app used to guess.
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> Phrases { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // "Hey B" IS THREE TOKENS, AND THE BOOK CALLS THAT CAUTION.
            // WakePhraseBook.MinReliableTokens is 4, and its own note measures a
            // four-token phrase at 12/12 where a shorter one is not dependable
            // across a room. Measured on a P30 with the microphone confirmed
            // capturing: neither a synthesised nor a human "Hey B" ever fired.
            //
            // "Hey Circle AI" clears both of the book's tests - four or more
            // tokens, and "AI" is not on the everyday-words list, so it will not
            // wake up mid-conversation the way an all-common phrase does. Note
            // "Hey Circle" would NOT clear the second test: "hey" and "circle"
            // are both everyday words.
            //
            // First in the list, so it is the default - DeviceWakePhrases falls
            // back to all[0] when nobody has chosen.
            //
            // "Hey B" stays as a second option rather than being deleted: it is
            // the product's name, somebody may already have chosen it, and the
            // two share no token prefix so neither shadows the other.
            ["en"] = ["Hey Circle AI", "Hey B"],

            // LONGEST FIRST, AND ROMANISED FIRST WITHIN THAT, because BestFor
            // takes the first candidate the bundle's tokenizer can represent and
            // that tokenizer is 500 English sub-words - no kana, no han, no
            // hangul. A script the model cannot see scores Unusable and is
            // skipped, so a native-script entry is a courtesy to whoever reads
            // this file rather than something the wake model will ever hear.
            //
            // EACH ONE IS A GREETING SOMEBODY WOULD ACTUALLY SAY, not "hey" with
            // a name bolted on. The point of a wake phrase in your own language
            // is that it is a thing you would say out loud without feeling
            // foolish - and the longer, more natural form is also the one with
            // enough tokens to survive a room. The two goals agree here, which
            // is unusual and worth taking.
            ["ja"] = ["Moshi moshi B san", "Bee san", "ビーさん", "ビーさま"],
            ["ko"] = ["Annyeong B nim", "Bee nim", "비 님"],
            ["zh"] = ["Ni hao Xiao B", "Xiao B", "小B"],
            ["yue"] = ["Nei hou Siu B", "Siu B", "小B"],

            // SOUTH AFRICA FIRST, because that is who this is for. Every one of
            // these is the ordinary greeting in that language followed by the
            // name - "sawubona" and "molo" and "dumela" are what people actually
            // open with, and all of them clear four tokens comfortably where a
            // bare "Hey B" does not.
            ["zu"]  = ["Sawubona B", "Sawubona Circle"],
            ["xh"]  = ["Molo B", "Molo Circle"],
            ["af"]  = ["Haai Circle AI", "Hallo B"],
            ["st"]  = ["Dumela B", "Dumela Circle"],
            ["tn"]  = ["Dumela B", "Dumela Circle"],
            ["nso"] = ["Dumela B", "Dumela Circle"],
            ["ts"]  = ["Avuxeni B", "Avuxeni Circle"],
            ["ve"]  = ["Ndaa B", "Ndaa Circle"],
            ["ss"]  = ["Sawubona B", "Sawubona Circle"],
            ["nr"]  = ["Lotjhani B", "Lotjhani Circle"],

            ["sw"] = ["Habari B", "Habari Circle"],
            ["am"] = ["Selam B", "Selam Circle"],
            ["ha"] = ["Sannu B", "Sannu Circle"],
            ["yo"] = ["Bawo ni B", "Bawo B"],
            ["ig"] = ["Ndewo B", "Ndewo Circle"],

            ["fr"] = ["Salut Circle AI", "Bonjour B"],
            ["es"] = ["Hola Circle AI", "Oye B"],
            ["pt"] = ["Ola Circle AI", "Ola B"],
            ["nl"] = ["Hallo Circle AI", "Hallo B"],
            ["hi"] = ["Namaste B", "Namaste Circle"],
            ["bn"] = ["Nomoshkar B", "Nomoshkar Circle"],
            ["ur"] = ["Salam B", "Salam Circle"],
            ["ar"] = ["Marhaba B", "Salam B"],
            ["ru"] = ["Privet Circle AI", "Privet B"],
            ["id"] = ["Halo Circle AI", "Halo B"],
            ["vi"] = ["Xin chao B", "Xin chao Circle"],
            ["th"] = ["Sawasdee B", "Sawasdee Circle"],
        };

    /// <summary>The phrases that ship for a language, or empty when there are none.</summary>
    /// <remarks>
    /// EMPTY RATHER THAN ENGLISH. Falling back to "Hey B" is what the listener
    /// does at the very bottom of the stack, so that a phone with no usable phrase
    /// still answers to something; but a SCREEN that falls back is a screen that
    /// lies, because the person reading it came to find out what to say.
    /// </remarks>
    public static IReadOnlyList<string> For(string? language)
        => Root(language) is { } code && Phrases.TryGetValue(code, out var list)
            ? list
            : [];

    /// <summary>Whether the app arrives knowing how to be woken in this language.</summary>
    public static bool Has(string? language) => For(language).Count > 0;

    /// <summary>The language part of a tag, so "ja-JP" finds "ja".</summary>
    private static string? Root(string? tag)
    {
        var code = tag?.Trim();
        if (string.IsNullOrEmpty(code)) return null;
        var cut = code.IndexOf('-');
        return cut > 0 ? code[..cut] : code;
    }
}
