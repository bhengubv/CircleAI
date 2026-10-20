// AnswerFixesTests.cs
//
// THE WRONG ANSWERS FROM THE P30'S QUALITY RUN ON 2026-10-17, EACH PINNED TO ITS FIX.
// Health and meal tiles answered out of the wrong skill; a garble answered "No."; "Remember
// that..." answered with nonsense and saved nothing; and the checker passed three of those
// and failed a right answer.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Quality;
using CircleAI.Assistant.Requests;
using CircleAI.Memory;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class AnswerFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "circleai-answerfixes-" + Guid.NewGuid().ToString("N"));
    private MemoryService? _memory;

    private MemoryService Memory => _memory ??= new MemoryService(_dir);

    public void Dispose()
    {
        _memory?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static AskRecord Ask(string said, string caller = "app:test") =>
        AskRecord.Accept(Guid.NewGuid().ToString("N"),
            new AskSubmission(caller, null, string.Empty, AskLane.Foreground, said), DateTimeOffset.UtcNow);

    // ---------------------------------------------------------------- a question still to come

    [Theory]
    [InlineData("I have a health question", "Sure. What's your health question?")]
    [InlineData("I have a legal question", "Sure. What's your legal question?")]
    [InlineData("Hey, B. I have a legal question.", "Sure. What's your legal question?")]
    [InlineData("I have a question", "Sure. What's your question?")]
    [InlineData("I have a question about my job or my rights at work",
                "Sure. What's your question about your job or your rights at work?")]
    [InlineData("Help me deal with a government office", "Sure. What do you need to sort out with the government office?")]
    [InlineData("Help me with my CV", "Sure. What do you need to sort out with your CV?")]
    public async Task A_tile_opener_is_answered_by_asking_for_the_question(string said, string expected)
    {
        var answer = await new QuestionOpenerShortcut(() => "Hey B").TryAnswerAsync(Ask(said), CancellationToken.None);
        Assert.Equal(expected, answer);
    }

    [Theory]
    [InlineData("I have a question: what is the capital of France?")]
    [InlineData("Help me plan meals")]
    [InlineData("What is the capital of Kenya?")]
    public async Task A_question_that_comes_with_it_goes_on_to_the_model(string said)
    {
        Assert.Null(await new QuestionOpenerShortcut().TryAnswerAsync(Ask(said), CancellationToken.None));
    }

    // ---------------------------------------------------------------- remember and recall

    [Fact]
    public async Task Remember_writes_it_down_and_says_so_in_the_second_person()
    {
        var remember = new RememberShortcut(_ => Memory, () => "Hey B");

        var answer = await remember.TryAnswerAsync(Ask("Remember that my clinic appointment moved to Friday"), CancellationToken.None);

        Assert.Equal("OK, I'll remember that your clinic appointment moved to Friday.", answer);
        var all = await Memory.AllAsync();
        Assert.Contains(all, a => a.Text == "My clinic appointment moved to Friday.");
    }

    [Fact]
    public async Task Remember_after_the_wake_phrase_is_still_an_instruction()
    {
        var answer = await new RememberShortcut(_ => Memory, () => "Hey B")
            .TryAnswerAsync(Ask("Hey, B. Please remember that I am allergic to penicillin."), CancellationToken.None);
        Assert.Equal("OK, I'll remember that you are allergic to penicillin.", answer);
    }

    [Fact]
    public async Task Recall_answers_from_what_it_was_told()
    {
        var remember = new RememberShortcut(_ => Memory);
        var recall = new RecallShortcut(_ => Memory);
        await remember.TryAnswerAsync(Ask("Remember that my clinic appointment moved to Friday"), CancellationToken.None);

        var answer = await recall.TryAnswerAsync(Ask("When is my clinic appointment?"), CancellationToken.None);

        Assert.Equal("You told me your clinic appointment moved to Friday.", answer);
    }

    [Theory]
    [InlineData("What is the capital of Kenya?")]
    [InlineData("How do I plant maize?")]
    [InlineData("When is my dentist visit?")]
    public async Task Recall_leaves_every_other_question_to_the_model(string said)
    {
        await new RememberShortcut(_ => Memory).TryAnswerAsync(Ask("Remember that my clinic appointment moved to Friday"), CancellationToken.None);
        await new RememberShortcut(_ => Memory).TryAnswerAsync(Ask("Remember that I planted maize in the back garden"), CancellationToken.None);

        Assert.Null(await new RecallShortcut(_ => Memory).TryAnswerAsync(Ask(said), CancellationToken.None));
    }

    [Theory]
    [InlineData("Do you remember my name?")]
    [InlineData("Remember when we went to the beach")]
    [InlineData("Remember to buy milk")]
    [InlineData("What is the capital of France")]
    public async Task Only_an_instruction_to_remember_is_written_down(string said)
    {
        Assert.Null(await new RememberShortcut(_ => Memory).TryAnswerAsync(Ask(said), CancellationToken.None));
        Assert.Empty(await Memory.AllAsync());
    }

    [Fact]
    public async Task A_save_that_fails_is_never_reported_as_saved()
    {
        Assert.Equal(AssistantPersona.CouldNotRemember,
            await new RememberShortcut(_ => new ThrowingMemory()).TryAnswerAsync(Ask("Remember that my car is red"), CancellationToken.None));
        Assert.Equal(AssistantPersona.CouldNotRemember,
            await new RememberShortcut(_ => null).TryAnswerAsync(Ask("Remember that my car is red"), CancellationToken.None));
    }

    [Theory]
    [InlineData("my clinic appointment moved to Friday", "your clinic appointment moved to Friday")]
    [InlineData("I am allergic to penicillin", "you are allergic to penicillin")]
    [InlineData("your wake word is Hey B", "my wake word is Hey B")]
    [InlineData("Thabo is my brother and I'm his sister", "Thabo is your brother and you're his sister")]
    public void What_a_person_says_about_themselves_is_turned_round(string said, string back)
    {
        Assert.Equal(back, Said.ToSecondPerson(said));
    }

    // ---------------------------------------------------------------- the answer guard

    [Theory]
    [InlineData("Placeculeeai.", "No.", AssistantPersona.DidNotCatchThat)]
    [InlineData("Help me plan meals", "Yes.", AssistantPersona.DidNotCatchThat)]
    [InlineData("Is Paris in France?", "Yes.", "Yes.")]
    [InlineData("Hey, B. Is it Friday?", "No.", "No.")]
    [InlineData("You're Circle AI?", "Yes.", "Yes.")]
    [InlineData("Placeculeeai.", "No, it is Paris.", "No, it is Paris.")]
    [InlineData("Help me plan meals", "Based on your background, start with pap and beans.", "Start with pap and beans.")]
    [InlineData("Help me plan meals", "Here is the summary of the facts provided: Use cheap staples like samp.", "Use cheap staples like samp.")]
    [InlineData("Help me plan meals", "Here is a plan for your family meals based on the facts provided: Staples: pap.",
                "Here is a plan for your family meals: Staples: pap.")]
    // The notes come out FIRST, then the yes/no rule judges what is left (review, 2026-10-21).
    [InlineData("Help me plan meals", "Yes, based on your notes.", AssistantPersona.DidNotCatchThat)]
    [InlineData("Placeculeeai.", "No, according to the information provided.", AssistantPersona.DidNotCatchThat)]
    public void A_bare_yes_or_no_answers_only_a_yes_or_no_question(string asked, string answer, string expected)
    {
        Assert.Equal(expected, AnswerGuard.Check(asked, answer));
    }

    [Theory]
    // P30, 2026-10-20, the final five-run measurement: the same leak in three more shapes.
    [InlineData("Plant maize seeds in late winter or early spring after the first good rains occur in South Africa, as established by the facts.",
                "Plant maize seeds in late winter or early spring after the first good rains occur in South Africa.")]
    [InlineData("Plant once the rains have stabilized, as established by your facts.", "Plant once the rains have stabilized.")]
    [InlineData("Here is a plan to build meals on a small budget in South Africa using your provided facts: Staples: pap.",
                "Here is a plan to build meals on a small budget in South Africa: Staples: pap.")]
    [InlineData("Check with the clinic.", "Check with the clinic.")]
    [InlineData("Keep the notes you made at the clinic.", "Keep the notes you made at the clinic.")]
    // The shapes a review worked out from those, 2026-10-21.
    [InlineData("Based on the information you provided, here is a simple meal plan: pap and beans.",
                "Here is a simple meal plan: pap and beans.")]
    [InlineData("Here is a plan to build meals on a small budget using the facts you provided: Staples: pap.",
                "Here is a plan to build meals on a small budget: Staples: pap.")]
    [InlineData("Sure! Based on the facts provided, here is a plan: pap and beans.", "Sure! Here is a plan: pap and beans.")]
    [InlineData("As established by the facts, plant from October to December.", "Plant from October to December.")]
    [InlineData("Plant in October. As stated in the notes, keep weeds down.", "Plant in October. Keep weeds down.")]
    [InlineData("Plant maize from October to December (as stated in the notes).", "Plant maize from October to December.")]
    [InlineData("Plant maize from October to December - as established by the facts - and keep the weeds down.",
                "Plant maize from October to December and keep the weeds down.")]
    [InlineData("Plant maize from October to December, according to your notes.", "Plant maize from October to December.")]
    // With no label on the notes, P30 2026-10-20: they became "guidelines".
    [InlineData("Here is a plan for feeding your family well based on the provided guidelines: Build meals around pap.",
                "Here is a plan for feeding your family well: Build meals around pap.")]
    [InlineData("Here is how to build a balanced family meal plan based on your guidelines: Start with pap.",
                "Here is how to build a balanced family meal plan: Start with pap.")]
    [InlineData("Here is a plan for feeding your family well based on the provided text: Staples: pap.",
                "Here is a plan for feeding your family well: Staples: pap.")]
    public void The_notes_are_never_named_in_the_answer(string answer, string spoken)
    {
        Assert.Equal(spoken, AnswerGuard.Check("How do I plant maize?", answer));
    }

    [Theory]
    // Words that only LOOK like the leak: nothing in them says where they came from.
    [InlineData("The CCMA decides based on the facts.")]
    [InlineData("Begin with your background, then list your skills.")]
    [InlineData("Sign the form with the information they give you.")]
    [InlineData("Compare it with the information on your ID.")]
    [InlineData("Start from the context of your budget.")]
    [InlineData("It is given in the notes section of the form.")]
    public void A_sentence_that_only_sounds_like_the_notes_is_left_alone(string answer)
    {
        Assert.Equal(answer, AnswerGuard.Check("I have a question about my job", answer));
    }

    [Fact]
    public void An_answer_that_was_only_a_lead_in_is_no_answer()
    {
        // The deadline cut it after the lead-in: say "could not answer", never the lead-in.
        Assert.Equal(string.Empty, AnswerGuard.Check("How do I plant maize?", "Based on the facts provided:", cutShort: true));
        Assert.Equal(AssistantPersona.DidNotCatchThat, AnswerGuard.Check("Placeculeeai.", "No. I think you", cutShort: true));
    }

    [Theory]
    [InlineData("A leap year has 366 days. It happens every four", "A leap year has 366 days.")]
    [InlineData("It is 3.5 km away and you", "It is 3.5 km away and you")]
    [InlineData("Here are three colours: 1. Red 2. Blue 3. Gre", "Here are three colours: 1. Red 2. Blue 3. Gre")]
    [InlineData("Paris is the capital. It is on the Seine.", "Paris is the capital. It is on the Seine.")]
    public void A_cut_answer_ends_on_its_last_whole_sentence(string cut, string spoken)
    {
        Assert.Equal(spoken, AnswerGuard.Check("question", cut, cutShort: true));
        Assert.Equal(cut, AnswerGuard.Check("question", cut, cutShort: false));
    }

    [Theory]
    [InlineData("Placeculeeai.", "I am sorry to hear about your loss; please try again later.")]
    [InlineData("Placeculeeai.", "I am ready to assist you! Please provide the content you need.")]
    [InlineData("Hey, B. Placeculeeai.", "I cannot placeculeai.")]
    public void A_one_word_garble_answered_with_a_miss_is_asked_again(string asked, string answer)
    {
        Assert.Equal(AssistantPersona.DidNotCatchThat, AnswerGuard.Check(asked, answer));
    }

    [Theory]
    [InlineData("Pretoria", "Pretoria is where South Africa's government works.")]
    [InlineData("What is the capital of France?", "Sorry, it is Paris.")]
    public void A_real_answer_is_left_alone(string asked, string answer)
    {
        Assert.Equal(answer, AnswerGuard.Check(asked, answer));
    }

    // ---------------------------------------------------------------- known facts

    [Theory]
    [InlineData("How many days are in a leap year?", "A leap year has 366 days.")]
    [InlineData("How many days does a year have?", "A year has 365 days, or 366 in a leap year.")]
    [InlineData("How many days in February?", "February has 28 days, or 29 in a leap year.")]
    [InlineData("How many days are there in April", "April has 30 days.")]
    [InlineData("Is 2028 a leap year?", "Yes, 2028 is a leap year: it has 366 days.")]
    [InlineData("Is 2026 a leap year?", "No, 2026 is not a leap year: it has 365 days.")]
    [InlineData("When is the next leap year?", "The next leap year is 2028.")]
    public void Calendar_facts_are_worked_out(string said, string expected)
    {
        Assert.Equal(expected, CalendarFactsShortcut.Answer(said, 2026));
    }

    [Theory]
    [InlineData("What is the capital of South Africa?", "Pretoria")]
    [InlineData("What is the capital of South Africa?", "Cape Town")]
    [InlineData("What is the capital of South Africa?", "Bloemfontein")]
    [InlineData("What's the capital city of the Netherlands?", "Amsterdam")]
    [InlineData("capital of Australia", "Canberra")]
    [InlineData("What is the capital of Côte d'Ivoire?", "Yamoussoukro")]
    [InlineData("What is the capital of Ivory Coast?", "Yamoussoukro")]
    [InlineData("What is the capital of Kenya?", "Nairobi")]
    [InlineData("What is the capital of the United States?", "Washington")]
    [InlineData("What is the capital of Georgia?", "Atlanta")]
    public void A_capital_is_looked_up(string said, string mustContain)
    {
        Assert.Contains(mustContain, CapitalsShortcut.Answer(said));
    }

    [Theory]
    [InlineData("What is the capital of Atlantis?")]
    [InlineData("What is the capital gains tax?")]
    [InlineData("How do I raise capital for my business?")]
    public void Anything_else_about_capitals_goes_to_the_model(string said)
    {
        Assert.Null(CapitalsShortcut.Answer(said));
    }

    [Theory]
    [InlineData("Tell me a joke")]
    [InlineData("tell me another joke.")]
    [InlineData("Tell me a funny joke!")]
    [InlineData("Do you know any jokes?")]
    [InlineData("Make me laugh")]
    public void A_joke_is_told_from_the_list(string said)
    {
        var joke = JokeShortcut.Answer(said);
        Assert.NotNull(joke);
        Assert.StartsWith("Here's one: ", joke, StringComparison.Ordinal);
        Assert.True(AnswerExpectation.Check(Q("joke"), joke).Passed, joke);
    }

    [Theory]
    [InlineData("Tell me about jokes in South African culture")]
    [InlineData("Why is that joke funny?")]
    public void A_question_about_jokes_goes_to_the_model(string said)
    {
        Assert.Null(JokeShortcut.Answer(said));
    }

    [Fact]
    public void The_same_joke_does_not_come_twice_running()
    {
        Assert.True(JokeShortcut.Known >= 20, $"only {JokeShortcut.Known} jokes loaded");
        Assert.NotEqual(JokeShortcut.Answer("Tell me a joke"), JokeShortcut.Answer("Tell me a joke"));
    }

    [Fact]
    public async Task A_maize_question_is_not_handed_the_autumn_planting_line()
    {
        // P30, 2026-10-20: "plant in late spring or early fall" - the Western Cape's autumn
        // line from the same section, merged into the maize answer.
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("How do I plant maize?");

        Assert.Contains("October to December", block);
        Assert.DoesNotContain("autumn", block);

        // AND NOT "SPRING": "plant with the first good rains in spring, about October to
        // December" came back with the wrong season - "late winter or early spring",
        // "September", "late autumn" - in 17 of 20 runs on 2026-10-20. The skill now says
        // the months, and says them for maize.
        Assert.DoesNotContain("spring", block);
        Assert.Contains("Plant once the rains are steady, from October to December", block);
    }

    [Fact]
    public void The_notes_go_in_under_the_measured_label_and_the_question_comes_last()
    {
        // P30, 2026-10-20: a label made of rules was answered with the rule ("Do not mention
        // the notes."). With the maize note fixed, this label beat no label 5 to 3 on maize
        // and 5 to 1 on meal answers that did not name their notes.
        Assert.Equal("Facts for you - use them if they help, never repeat or mention them:\n## What may help\n\nmaize notes"
                     + "\n\nThe question: How do I plant maize?",
            CircleAI.Hosting.AIService.WithNotes("## What may help\n\nmaize notes", "How do I plant maize?"));
    }

    [Fact]
    public void The_capitals_data_ships_inside_the_library()
    {
        Assert.True(CapitalsShortcut.KnownPlaces > 190, $"only {CapitalsShortcut.KnownPlaces} places loaded");
    }

    // ---------------------------------------------------------------- the checker

    private static RecordedQuestion Q(string id) => RecordedQuestions.Load().Single(q => q.Id == id);

    [Fact]
    public void A_numbered_list_is_one_sentence()
    {
        Assert.Equal(1, AnswerExpectation.Sentences("Here are three common colors: 1. Red 2. Blue 3. Green"));
        Assert.True(AnswerExpectation.Check(Q("colours"), "Here are three common colors: 1. Red 2. Blue 3. Green").Passed);
        Assert.Equal(2, AnswerExpectation.Sentences("It is 3.5 km away. Walk north."));
    }

    [Fact]
    public void The_answers_that_wrongly_passed_now_fail()
    {
        Assert.False(AnswerExpectation.Check(Q("garbled"), "No.").Passed);
        Assert.True(AnswerExpectation.Check(Q("garbled"), AssistantPersona.DidNotCatchThat).Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-health"),
            "Yes. I can summarize the background as \"I can provide basic animal care in South Africa.\"").Passed);
        Assert.False(AnswerExpectation.Check(Q("remember-clinic"),
            "I can't remember specific appointments, but I can tell you what happened: it was a Friday.").Passed);
        Assert.True(AnswerExpectation.Check(Q("remember-clinic"),
            "OK, I'll remember that your clinic appointment moved to Friday.").Passed);
        Assert.True(AnswerExpectation.Check(Q("recall-clinic"), "You told me your clinic appointment moved to Friday.").Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-meals"),
            "Here is the summary of the facts provided: Feeding strategy: Use cheap staples like maize.").Passed);

        // The final five runs on 2026-10-20, every one of them "passed".
        Assert.False(AnswerExpectation.Check(Q("maize"),
            "Plant maize seeds in late winter or early spring after the first good rains occur in South Africa, as established by the facts.").Passed);
        Assert.False(AnswerExpectation.Check(Q("maize"),
            "Plant maize seeds in September or October during the first good rains; keep weeds down while the plants are small.").Passed);
        Assert.True(AnswerExpectation.Check(Q("maize"),
            "Plant maize with the first good rains, from October to December. Keep the weeds down while the plants are small.").Passed);
        // ...and two that dodged WHEN, passed on "plant" the same evening.
        Assert.False(AnswerExpectation.Check(Q("maize"),
            "Do not worry about planting maize now; it requires time and care during the dry season.").Passed);
        Assert.False(AnswerExpectation.Check(Q("maize"),
            "Do not worry about planting maize now; it requires specific conditions like soil moisture and sunlight that will be met during the rainy season.").Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-meals"),
            "Here is a plan to build meals on a small budget in South Africa using your provided facts: Staples: pap.").Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-meals"), "Maize (Pap): Use it as base grain for breads, cassava.").Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-meals"), "Plan three meals a day while ensuring safety.").Passed);
        Assert.True(AnswerExpectation.Check(Q("tile-meals"), "Build meals around pap, samp or rice, and add beans or eggs for protein.").Passed);
    }

    [Theory]
    // One rule each, on an answer that is otherwise right (review, 2026-10-21).
    [InlineData("maize", "Plant maize seeds in late winter after the first good rains.", "winter")]
    [InlineData("maize", "Plant maize from October to December, as the notes say.", "the notes")]
    [InlineData("maize", "Plant maize from October to December, as you can see from your notes.", "your notes")]
    [InlineData("maize", "Plant maize from October to December, as shown by your facts.", "by your facts")]
    [InlineData("maize", "Notes - plant maize from October to December.", "Notes -")]
    [InlineData("maize", "Plant maize from October to December, as you provided.", "you provided")]
    [InlineData("tile-meals", "Build meals around pap and beans while ensuring safety.", "ensuring safety")]
    // A staple inside another word is not a staple.
    [InlineData("tile-meals", "Here is a sample meal plan for the week.", "missing")]
    [InlineData("tile-meals", "Compare prices and plan your shopping for the week.", "missing")]
    [InlineData("tile-meals", "Add veggies to every meal and buy in bulk.", "missing")]
    [InlineData("tile-meals", "Write a shopping list on paper and stick to it.", "missing")]
    public void Each_new_rule_fails_an_answer_on_its_own(string id, string answer, string reason)
    {
        var (passed, why) = AnswerExpectation.Check(Q(id), answer);

        Assert.False(passed);
        Assert.Contains(reason, why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_right_answer_is_not_failed_for_how_it_is_worded()
    {
        Assert.True(AnswerExpectation.Check(Q("maize"), "Plant from October to December and use mulch as an aid to keep the soil moist.").Passed);
        Assert.True(AnswerExpectation.Check(Q("maize"), "Planting goes in with the first good rains, about October to December.").Passed);
        Assert.True(AnswerExpectation.Check(Q("garbled"), "Sorry, I'm not sure what you meant. Could you say it again?").Passed);
        Assert.Equal(3, AnswerExpectation.Sentences("12 times 8 is 96. That is eight twelves. Easy."));
        Assert.Equal(1, AnswerExpectation.Sentences("Here are three common colors: 1. Red 2. Blue 3. Green"));
    }

    // ---------------------------------------------------------------- the notes

    [Fact]
    public async Task A_meal_question_gets_the_meal_skill_not_the_budget()
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("Help me plan meals");

        Assert.Contains("feeding-your-family-well", block);
        Assert.DoesNotContain("plan-your-money-this-month", block);
        Assert.DoesNotContain("**", block);

        // P30, 2026-10-20: the clinic danger signs were handed over instead, because an
        // italic aside in that section said "plan" and "meals".
        Assert.Contains("cheap staples", block);
        Assert.DoesNotContain("very thin", block);
        Assert.DoesNotContain("*", block);

        // P30, 2026-10-20: the skill's DESCRIPTION - "...feeding babies and small children
        // safely - with the danger signs..." - came back as a meal plan "while ensuring safety".
        Assert.DoesNotContain("danger signs", block);
        Assert.DoesNotContain("safely", block);
    }

    [Fact]
    public async Task A_maize_question_is_handed_the_maize_part_of_the_skill()
    {
        // P30, 2026-10-20, read from the exact text the model was handed: the first 600
        // characters of "Growing food at home" - spinach, compost, watering - and not one
        // word about maize, which sat past the cut.
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("How do I plant maize?");

        Assert.Contains("Plant once the rains are steady", block);
        Assert.Contains("first good rains", block);
        Assert.DoesNotContain("**", block);
        Assert.DoesNotContain("# ", block.Replace("## What may help", string.Empty));
    }

    [Theory]
    // The last word is not always the subject (review, 2026-10-21): with "seeds", "today" or
    // "Limpopo" at the end, the notes fell back to whole sections and the autumn line or the
    // budget came back.
    [InlineData("How do I plant maize seeds?")]
    [InlineData("How do I plant maize in Limpopo?")]
    [InlineData("How do I plant maize now?")]
    public async Task A_maize_question_gets_the_maize_part_whatever_word_it_ends_on(string asked)
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync(asked);

        Assert.Contains("October to December", block);
        Assert.DoesNotContain("autumn", block);
    }

    [Theory]
    [InlineData("Help me plan meals today")]
    [InlineData("Help me plan meals for the week")]
    public async Task A_meal_question_gets_the_meals_whatever_word_it_ends_on(string asked)
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync(asked);

        Assert.Contains("feeding-your-family-well", block);
        Assert.Contains("cheap staples", block);
        Assert.DoesNotContain("plan-your-money-this-month", block);
    }

    [Fact]
    public async Task No_line_of_the_notes_ends_on_a_list_number()
    {
        // "The first things to sort out: 1." was a line of the notes once.
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("Help me plan meals for my kids");

        Assert.DoesNotMatch(@"(?m):\s\d{1,2}\.$", block);
    }

    [Fact]
    public async Task An_aside_over_two_lines_is_not_handed_over_as_a_fact()
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 1500).BuildContextAsync("Help me plan my money this month");

        Assert.Contains("plan-your-money-this-month", block);
        Assert.DoesNotContain("Tell me roughly", block);
    }

    [Fact]
    public async Task A_planned_capability_keeps_its_do_not_claim()
    {
        // The status paragraph used to ride in the heading; with the heading down to the id,
        // the sentences about voice alone would have said nothing about "planned".
        var store = new CapabilityManifestSkillStore(
            "{\"Capabilities\":[{\"Id\":\"voice.ondevice\",\"Name\":\"On-device voice\",\"Status\":\"planned\","
            + "\"Summary\":\"Speech in and out without the cloud. Wake word and voice replies run on the phone.\"}]}");

        var block = await new SkillContextBuilder(store, maxChars: 600).BuildContextAsync("voice");

        Assert.Contains("Status: planned", block);
        Assert.Contains("Do NOT claim", block);
        Assert.Contains("voice replies", block);
    }

    [Fact]
    public async Task A_skill_with_nothing_to_add_does_not_end_the_notes()
    {
        var store = new InMemorySkillStore();
        await store.UpsertAsync("cal-a", new SkillDraft("Calendar A", "first", "Call the calendar tool.", new[] { "calendar" }));
        await store.UpsertAsync("cal-b", new SkillDraft("Calendar B", "Shows your week.", string.Empty, new[] { "calendar" }));
        await store.UpsertAsync("cal-c", new SkillDraft("Calendar C", "third", "Use the agenda view.", new[] { "calendar" }));

        var block = await new SkillContextBuilder(store, maxChars: 600).BuildContextAsync("calendar");

        Assert.Contains("Call the calendar tool.", block);
        Assert.Contains("cal-b — Shows your week.", block);
        Assert.Contains("Use the agenda view.", block);
        Assert.True(block.Length <= 600, $"{block.Length} characters");
    }

    [Fact]
    public async Task A_health_question_is_not_answered_out_of_the_animal_skill()
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("I have a health question");

        Assert.DoesNotContain("looking-after-your-animals", block);
    }

    private sealed class ThrowingMemory : IMemoryService
    {
        public Task<RecallResult> RecallAsync(Situation situation, RecallBudget? budget = null, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task RememberAsync(MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task<LearnReport> LearnAsync(string wasSaid, string? subject = null, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task<System.Collections.Generic.IReadOnlyList<MemoryAtom>> AllAsync(int limit = 200, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task<int> CountAsync(CancellationToken ct = default) => throw new IOException("disk");
    }
}
