// IDtmfSendable.cs
//
// (3.3.0) Optional sister interface a host can layer on its
// IMediaStream implementation to support carrier-native out-of-band
// DTMF (e.g. Twilio's mark control frame, Telnyx Call Control
// send_dtmf, Plivo Audio Streaming control event). When the media
// stream doesn't implement this, the session falls back to in-band
// tones via DtmfToneGenerator.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

public interface IDtmfSendable
{
    ValueTask SendDtmfAsync(string digits, CancellationToken ct = default);
}
