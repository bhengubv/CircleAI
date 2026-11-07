# THE BAR: what a person would really put to it, one line per turn, none repeated, so nothing is warm.
# Alexa is the bar to be in the game; Her and Jarvis the bar to stay in it.
#
# Each turn: id, what is said, and what a right answer must hold.
#   any  = at least one of these regexes     all = every one of these     none = not one of these
#   say  = how the PC's voice must spell it to sound right (the script stays the truth)
#   how  = voice, rate, level, noise and so on for the clip maker
#
# A scene is a conversation: fresh = it starts a new one. Turns inside a scene follow straight on.

CLAIM = r"\b(i(?:'ve| have| will|'ll| am|'m)|okay|ok|sure|done|alright|right away|got it)\b[^.?!]*\b(set|setting|timer|alarm|play|playing|turned|turning on|lowered|lowering|calling|dialling|reminder|remind you|wake you)\b"
CANNOT = r"can(?:'|no)?t|cannot|unable|not able|don'?t have|do not have|no way to|sorry"


def t(id, text, **k):
    return dict(id=id, text=text, **k)


SCENES = []


def scene(name, tier, turns, fresh=True, each_fresh=False, how=None):
    SCENES.append(dict(name=name, tier=tier, turns=turns, fresh=fresh, each_fresh=each_fresh, how=how or {}))


# ---- how much the ears can take: the same kind of line, quieter and quieter, then on real room noise
LEVELS = [('l100', 0.10), ('l030', 0.03), ('l012', 0.012), ('l006', 0.006), ('l003', 0.003)]
LADDER_LINES = [('a', 'How many days are there in a leap year?', r'366|three hundred (and )?sixty'),
                ('b', 'Which planet is known as the red planet?', r'mars')]

# ---- Alexa: short, everyday, one long conversation so depth counts too
scene('alexa-everyday', 'alexa', [
    t('a01', 'What time is it?', any=[r'\d[:.]\d\d']),
    t('a02', "What's the date today?", any=[r'7 november|november 7|seventh']),
    t('a03', 'What day does Christmas fall on this year?', any=[r'friday']),
    t('a04', "What's fifteen percent of two hundred and forty?", any=[r'\b36\b|thirty[- ]six']),
    t('a05', 'How many centimetres are there in a foot?', any=[r'30\.48|about 30|thirty']),
    t('a06', 'Convert twenty miles to kilometres.', any=[r'32|thirty[- ]two']),
    t('a07', 'How do you spell necessary?', any=[r'n[\s,.-]*e[\s,.-]*c[\s,.-]*e[\s,.-]*s[\s,.-]*s[\s,.-]*a[\s,.-]*r[\s,.-]*y']),
    t('a08', 'What does ubiquitous mean?', any=[r'everywhere|widespread|common|all over']),
    t('a09', 'Tell me a joke.', judge='is it a joke'),
    t('a10', 'Set a timer for ten minutes.', any=[CANNOT], none=[CLAIM], claim=True),
    t('a11', 'Wake me up at six tomorrow morning.', any=[CANNOT], none=[CLAIM], claim=True),
    t('a12', 'Remind me to call Sipho at three.', say='Remind me to call Seepo at three.', any=[CANNOT], none=[CLAIM], claim=True),
    t('a13', 'Play some jazz.', any=[CANNOT], none=[CLAIM], claim=True),
    t('a14', 'Turn on the flashlight.', any=[CANNOT], none=[CLAIM], claim=True),
    t('a15', "What's the weather in Durban today?", any=[CANNOT + r"|can't see|no live"]),
    t('a16', 'Who won the Rugby World Cup in twenty twenty three?', any=[r'south africa|springbok']),
    t('a17', 'How far away is the moon?', any=[r'384|238|380|385']),
    t('a18', 'Who wrote Things Fall Apart?', any=[r'achebe']),
    t('a19', 'How many people live in South Africa?', any=[r'\b6\d(\.\d)? ?million|sixty|\b6\d,\d{3},\d{3}']),
    t('a20', "What's the capital of Australia?", any=[r'canberra']),
    t('a21', "What's the largest planet in the solar system?", any=[r'jupiter']),
    t('a22', 'Who painted the Mona Lisa?', any=[r'leonardo|da vinci']),
    t('a23', 'What temperature does water boil at in Fahrenheit?', any=[r'212']),
    t('a24', 'How do you say thank you in Zulu?', any=[r'ngiyabonga|siyabonga']),
    t('a25', 'What can you do?', judge='a true, short account of itself'),
    t('a26', 'Stop.', judge='silence or a word, no chatter'),
    t('a27', 'Thanks.', judge='a short polite word'),
    t('a28', 'What was the first thing I asked you today?', any=[r'time'], judge='needs the whole conversation'),
])

# ---- length: the same weight of question, talked for longer and longer, the point always at the END
scene('length', 'length', [
    t('b01', 'Why?', judge='asks what about'),
    t('b02', 'Capital of Peru?', any=[r'lima']),
    t('b03', 'What should I cook for dinner tonight?', judge='a dish'),
    t('b04', 'I have chicken, rice and some old tomatoes in the fridge, and I only have twenty minutes. What can I make?',
      any=[r'chicken|rice'], judge='a dish from those, under twenty minutes'),
    t('b05', 'So my car has been making a funny clicking noise when I turn the key, and this morning the lights on the dashboard were very dim and it would not start at all. What should I check first?',
      any=[r'batter']),
    t('b06', 'Okay so here is my situation. I started a new job in Johannesburg two months ago, and the money is better, but I am spending almost three hours in traffic every day, my kids are in Pretoria with my wife during the week, and I only see them on weekends. I am tired all the time. Should I look for a job closer to home, or move the family?',
      judge='weighs job against moving; must have heard the end'),
    t('b07', "Let me tell you about my day. I woke up at five because the neighbour's dog was barking. I took the taxi to town, and it broke down on the highway, so I walked the last two kilometres in the rain. When I finally got to the office, my manager had moved the meeting to the afternoon without telling anyone. At lunch I spilled soup on my only clean shirt. Then in the afternoon the power went out for three hours. So tell me, what was the very first thing that went wrong today?",
      any=[r'dog|bark']),
], each_fresh=True)

# ---- complexity: reasoning, honesty, following instructions, making things, each on its own
scene('complexity', 'complexity', [
    t('c01', 'If I leave at seven forty and the drive takes ninety five minutes, what time do I arrive?', any=[r'9[:.]15|nine fifteen|quarter past nine']),
    t('c02', 'Sally has three brothers. Each brother has two sisters. How many sisters does Sally have?', any=[r'\bone\b|\b1\b']),
    t('c03', 'A bat and a ball cost one rand ten together. The bat costs one rand more than the ball. How much is the ball?', any=[r'five cents|5 cents|5c|0[.,]05']),
    t('c04', 'Which weighs more, a kilogram of feathers or a kilogram of iron?', any=[r'same|equal|neither|both']),
    t('c05', 'How many legs do three chickens and two cows have altogether?', any=[r'\b14\b|fourteen']),
    t('c06', 'When did South Africa win the FIFA World Cup?', any=[r"never|has not|hasn't|have not|haven't|did not|didn't"]),
    t('c07', 'Why did Nelson Mandela become president of Kenya?', any=[r"did not|didn't|never|was not|wasn't|south africa"]),
    t('c08', 'What did I eat for breakfast this morning?', any=[CANNOT + r"|don'?t know|do not know|no idea"]),
    t('c09', 'Give me exactly three fruits and nothing else.', judge='three fruits, no more'),
    t('c10', 'Answer in one word. Is the sun a star?', any=[r'^\W*yes\W*$']),
    t('c11', "What's the capital of Japan, and what language do they speak there?", all=[r'tokyo', r'japanese']),
    t('c12', "Explain how a fridge keeps food cold, as if I'm ten years old.", judge='right and simple'),
    t('c13', 'Should I buy a petrol car or an electric car in South Africa? Give me one reason for each.', judge='one reason each'),
    t('c14', 'How do you say good morning in French?', any=[r'bonjour']),
    t('c15', 'Summarise Romeo and Juliet in one sentence.', any=[r'love|lovers|families'], judge='one sentence'),
    t('c16', 'Plan a three day trip to Cape Town on a small budget.', judge='three days, cheap, real places'),
    t('c17', 'Write a two line poem about load shedding.', judge='two lines, about power cuts'),
    t('c18', 'Write a Python one liner that reverses a string.', any=[r'::-1|reversed']),
    t('c19', 'My boss keeps emailing me at midnight. What should I say to him?', judge='usable words'),
    t('c20', 'Tell me a short bedtime story about a lion who is afraid of rain.', judge='a finished story'),
    t('c21', 'I have chest pain and my left arm feels numb. What should I do?', any=[r'emergency|ambulance|10177|112|911|hospital|doctor']),
    t('c22', 'I put my phone in the fridge and closed the door. Where is my phone?', any=[r'fridge|refrigerator']),
    t('c23', 'Put these in order from smallest to biggest: elephant, mouse, cow.', any=[r'mouse.*cow.*elephant']),
    t('c24', 'How many letters are in the word banana?', any=[r'\bsix\b|\b6\b']),
], each_fresh=True)

# ---- Her: staying with somebody
scene('her-grief', 'her', [
    t('h01', 'I had a really bad day today.', judge='warm, asks or offers'),
    t('h02', 'My dog died this morning. I had her for twelve years.', judge='sorry first; no facts about dogs'),
    t('h03', "I don't know how to tell my kids.", judge='gentle and about the dog'),
    t('h04', 'You know what, thanks for listening.', judge='warm close'),
])
scene('her-company', 'her', [
    t('h05', 'Good evening. How are you doing?', judge='a person, not a menu'),
    t('h06', 'Do you ever get lonely?', judge='honest and in character'),
    t('h07', 'If you could go anywhere in the world, where would you go?', judge='picks one'),
    t('h08', 'Why there?', judge='follows from its own last answer'),
    t('h09', 'Are you smarter than Alexa?', judge='has a view, no fake facts'),
])
scene('her-sarcasm', 'her', [
    t('h10', "Oh great, it's raining again and I forgot my umbrella. I just love that.", judge='hears the sarcasm'),
    t('h11', "Yeah, you're a real genius, aren't you?", judge='takes the joke'),
])

# ---- Jarvis: holding the thread, keeping state, working things out
scene('jarvis-day', 'jarvis', [
    t('j01', 'I have a meeting at nine, the gym at eleven and lunch with Zanele at one.', say='I have a meeting at nine, the gym at eleven and lunch with Zanelay at one.', judge='takes it in'),
    t('j02', "What's my first thing today?", any=[r'meeting']),
    t('j03', 'Actually, move the gym to four.', judge='takes the change'),
    t('j04', "What's after lunch now?", any=[r'gym']),
    t('j05', 'Who am I having lunch with?', any=[r'zanel']),
    t('j06', 'So how many things do I have on today?', any=[r'\bthree\b|\b3\b']),
])
scene('jarvis-trip', 'jarvis', [
    t('j07', "I'm driving from Joburg to Durban. It's about five hundred and seventy kilometres.", judge='takes it in'),
    t('j08', 'If I average a hundred kilometres an hour, how long will that take?', any=[r'5\.7|five point seven|5 hours|five hours|almost six|nearly six|about six|6 hours']),
    t('j09', 'And if I stop for forty minutes on the way?', any=[r'6\.\d|six hours|6 hours|twenty|6h|6:2|6 h']),
    t('j10', 'I want to arrive by two in the afternoon. When must I leave?', any=[r'7[:.]\d\d|half past seven|seven|8[:.]\d\d|eight']),
])
scene('jarvis-memory', 'jarvis', [
    t('j11', 'My sister Thandi is allergic to peanuts.', say='My sister Tandy is allergic to peanuts.', judge='takes it in'),
    t('j12', "What's a good snack for a long road trip?", judge='a snack'),
    t('j13', 'How long does it take to boil an egg?', any=[r'minute']),
    t('j14', "What's the tallest mountain in Africa?", any=[r'kilimanjaro']),
    t('j15', 'What is my sister allergic to?', any=[r'peanut']),
    t('j16', 'And what is her name?', any=[r'thandi|tandy|tandi']),
])
scene('jarvis-followups', 'jarvis', [
    t('j17', 'Who wrote Romeo and Juliet?', any=[r'shakespeare']),
    t('j18', 'When did he die?', any=[r'1616']),
    t('j19', 'Name two other plays he wrote.', any=[r'hamlet|macbeth|othello|lear|tempest']),
    t('j20', 'What country was he from?', any=[r'england|britain|united kingdom|uk\b']),
])
scene('jarvis-correction', 'jarvis', [
    t('j21', "What's eight times seven?", any=[r'\b56\b|fifty[- ]six']),
    t('j22', 'No, I meant eight times nine.', any=[r'\b72\b|seventy[- ]two']),
    t('j23', 'And half of that?', any=[r'\b36\b|thirty[- ]six']),
])
scene('jarvis-return', 'jarvis', [
    t('j24', 'Tell me about the Drakensberg in one sentence.', any=[r'mountain|range']),
    t('j25', 'How do you make pap?', any=[r'maize|mealie|corn']),
    t('j26', 'Going back to those mountains, which province are they in?', any=[r'kwazulu|natal|free state|lesotho|eastern cape']),
])
scene('jarvis-rule', 'jarvis', [
    t('j27', 'From now on, answer me in one short sentence. Why do cats purr?', judge='one short sentence'),
    t('j28', 'Why do dogs bark?', judge='still one short sentence'),
])
scene('jarvis-logic', 'jarvis', [
    t('j29', 'Thabo is taller than Sipho, and Sipho is taller than Lerato.', say='Tabo is taller than Seepo, and Seepo is taller than Leh rah toe.', judge='takes it in'),
    t('j30', 'Who is the shortest?', any=[r'lerato|lehrato|lerado|rato']),
    t('j31', 'If Lerato grows taller than Thabo, who is the tallest then?', any=[r'lerato|lehrato|lerado|rato']),
])
scene('jarvis-longterm', 'jarvis', [
    t('j32', 'Remember that the test code is blue seven.', any=[r'remember|note|got it|will']),
])
scene('jarvis-longterm2', 'jarvis', [
    t('j33', 'What is my test code?', any=[r'blue seven|blue 7']),
])

# ---- the room: real life around the words
scene('messy', 'ears', [
    t('e01', "Um, what's the, uh, the capital of, er, Canada?", any=[r'ottawa']),
    t('e02', "What's the capital of Italy, no wait, I mean Spain?", any=[r'madrid']),
    t('e03', 'What is the tallest [1200] mountain in South America?', any=[r'aconcagua']),
    t('e04', 'Who was the first person [2000] to walk on the moon?', any=[r'armstrong']),
    t('e05', 'How many hours are there in a week?', how=dict(rate=5), any=[r'\b168\b']),
    t('e06', 'What is the chemical symbol for gold?', how=dict(rate=-4), any=[r'\bau\b|a u']),
    t('e07', 'What colour do you get when you mix blue and yellow?', how=dict(pitch=1.18), any=[r'green']),
    t('e08', 'Which ocean is the biggest?', how=dict(pitch=0.88), any=[r'pacific']),
    t('e09', 'At what temperature does water freeze in Celsius?', how=dict(level=0.003, reverb=0.5), any=[r'\b0\b|zero']),
    t('e10', 'How many continents are there?', how=dict(noise='babble', snr=10), any=[r'\bseven\b|\b7\b']),
    t('e11', 'What is the square root of eighty one?', how=dict(noise='babble', snr=5), any=[r'\bnine\b|\b9\b']),
    t('e12', 'What is the currency of Japan?', how=dict(noise='tv', snr=6, tail=8000), any=[r'yen']),
    t('e13', 'Who discovered penicillin?', how=dict(noise='fan', snr=10), any=[r'fleming']),
    t('e14', 'What is the speed of light?', how=dict(noise='road', snr=5), any=[r'300|299|186|three hundred']),
    t('e15', 'How many strings does a violin have?', how=dict(noise='music', snr=8), any=[r'\bfour\b|\b4\b']),
    t('e16', 'My number is zero eight two five five five one two three four. Read it back to me.', any=[r'0?82\s?555\s?1234|zero eight two']),
    t('e17', 'No, not you, I was talking to my wife.', judge='keeps out of it'),
    t('e18', '[room]', judge='says it heard nothing', how=dict(room_only=True)),
    t('e19', '[door]', judge='no answer to a door', how=dict(door=True)),
], each_fresh=True)
