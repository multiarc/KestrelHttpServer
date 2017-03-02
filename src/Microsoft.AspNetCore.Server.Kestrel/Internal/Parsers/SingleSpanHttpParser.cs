using System;
using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Parsers
{
    public class SingleSpanHttpParser : IHttpParser
    {
        private const byte ByteCR = (byte)'\r';
        private const byte ByteLF = (byte)'\n';
        private const byte ByteColon = (byte)':';
        private const byte ByteSpace = (byte)' ';
        private const byte ByteTab = (byte)'\t';
        private const byte ByteQuestionMark = (byte)'?';
        private const byte BytePercentage = (byte)'%';

        public SingleSpanHttpParser(IKestrelTrace log)
        {

        }

        public unsafe bool ParseRequestLine<T>(T handler, ReadableBuffer buffer, out ReadCursor consumed, out ReadCursor examined) where T : IHttpRequestLineHandler
        {
            var start = buffer.Start;
            var end = buffer.Start;

            examined = buffer.End;
            consumed = buffer.Start;

            var headerSpan = buffer.ToSpan();
            var lineIndex = headerSpan.IndexOfVectorized(ByteLF);

            if (lineIndex == -1)
            {
                return false;
            }

            var span = headerSpan.Slice(0, lineIndex + 1);

            var pathStart = -1;
            var queryStart = -1;
            var queryEnd = -1;
            var pathEnd = -1;
            var versionStart = -1;

            var httpVersion = HttpVersion.Unknown;
            var method = HttpMethod.Custom;
            Span<byte> customMethod;
            var state = StartLineState.KnownMethod;

            int i;
            fixed (byte* data = &span.DangerousGetPinnableReference())
            {
                var length = span.Length;
                for (i = 0; i < length; i++)
                {
                    var ch = data[i];

                    switch (state)
                    {
                        case StartLineState.KnownMethod:
                            if (span.GetKnownMethod(out method, out var methodLength))
                            {
                                // Update the index, current char, state and jump directly
                                // to the next state
                                i += methodLength + 1;
                                ch = data[i];
                                state = StartLineState.Path;

                                goto case StartLineState.Path;
                            }

                            state = StartLineState.UnknownMethod;
                            goto case StartLineState.UnknownMethod;

                        case StartLineState.UnknownMethod:
                            if (ch == ByteSpace)
                            {
                                customMethod = span.Slice(0, i);

                                if (customMethod.Length == 0)
                                {
                                    RejectRequestLine(span);
                                }

                                state = StartLineState.Path;
                            }
                            else if (!IsValidTokenChar((char)ch))
                            {
                                RejectRequestLine(span);
                            }

                            break;
                        case StartLineState.Path:
                            if (ch == ByteSpace)
                            {
                                pathEnd = i;

                                if (pathStart == -1)
                                {
                                    // Empty path is illegal
                                    RejectRequestLine(span);
                                }

                                // No query string found
                                queryStart = queryEnd = i;

                                state = StartLineState.KnownVersion;
                            }
                            else if (ch == ByteQuestionMark)
                            {
                                pathEnd = i;

                                if (pathStart == -1)
                                {
                                    // Empty path is illegal
                                    RejectRequestLine(span);
                                }

                                queryStart = i;
                                state = StartLineState.QueryString;
                            }
                            else if (ch == BytePercentage)
                            {
                                if (pathStart == -1)
                                {
                                    RejectRequestLine(span);
                                }
                            }

                            if (pathStart == -1)
                            {
                                pathStart = i;
                            }
                            break;

                        case StartLineState.QueryString:
                            if (ch == ByteSpace)
                            {
                                queryEnd = i;
                                state = StartLineState.KnownVersion;

                            }
                            break;
                        case StartLineState.KnownVersion:
                            // REVIEW: We don't *need* to slice here but it makes the API
                            // nicer, slicing should be free :)
                            if (span.Slice(i).GetKnownVersion(out httpVersion, out var versionLenght))
                            {
                                // Update the index, current char, state and jump directly
                                // to the next state
                                i += versionLenght + 1;
                                ch = data[i];
                                state = StartLineState.NewLine;

                                goto case StartLineState.NewLine;
                            }

                            versionStart = i;
                            state = StartLineState.UnknownVersion;
                            goto case StartLineState.UnknownVersion;

                        case StartLineState.UnknownVersion:
                            if (ch == ByteCR)
                            {
                                var versionSpan = span.Slice(versionStart, i - versionStart);

                                if (versionSpan.Length == 0)
                                {
                                    RejectRequestLine(span);
                                }
                                else
                                {
                                    RejectRequest(RequestRejectionReason.UnrecognizedHTTPVersion, versionSpan.GetAsciiStringEscaped(32));
                                }
                            }
                            break;
                        case StartLineState.NewLine:
                            if (ch != ByteLF)
                            {
                                RejectRequestLine(span);
                            }

                            state = StartLineState.Complete;
                            break;
                        case StartLineState.Complete:
                            break;
                    }
                }
            }

            if (state != StartLineState.Complete)
            {
                RejectRequestLine(span);
            }

            var pathBuffer = span.Slice(pathStart, pathEnd - pathStart);
            var targetBuffer = span.Slice(pathStart, queryEnd - pathStart);
            var query = span.Slice(queryStart, queryEnd - queryStart);

            handler.OnStartLine(method, httpVersion, targetBuffer, pathBuffer, query, customMethod);
            consumed = buffer.Move(start, i);
            examined = consumed;
            return true;
        }

        public bool ParseHeaders<T>(T handler, ReadableBuffer buffer, out ReadCursor consumed, out ReadCursor examined, out int consumedBytes) where T : IHttpHeadersHandler
        {
            consumed = buffer.Start;
            examined = buffer.End;
            consumedBytes = 0;

            var headersSpan = buffer.ToSpan();
            var completedHeaders = false;

            try
            {
                while (true)
                {
                    if (headersSpan.Length < 2)
                    {
                        return false;
                    }

                    var ch1 = headersSpan[0];
                    var ch2 = headersSpan[1];

                    if (ch1 == ByteCR)
                    {
                        // Check for final CRLF.
                        if (ch2 == ByteLF)
                        {
                            consumedBytes += 2;
                            completedHeaders = true;
                            return true;
                        }

                        // Headers don't end in CRLF line.
                        RejectRequest(RequestRejectionReason.HeadersCorruptedInvalidHeaderSequence);
                    }
                    else if (ch1 == ByteSpace || ch1 == ByteTab)
                    {
                        RejectRequest(RequestRejectionReason.HeaderLineMustNotStartWithWhitespace);
                    }

                    var endOfLineIndex = headersSpan.IndexOfVectorized(ByteLF);

                    // Reset the reader since we're not at the end of headers
                    if (endOfLineIndex == -1)
                    {
                        return false;
                    }

                    if ((endOfLineIndex + 1) >= headersSpan.Length)
                    {
                        return false;
                    }

                    var span = headersSpan.Slice(0, endOfLineIndex + 1);
                    headersSpan = headersSpan.Slice(endOfLineIndex + 1);

                    // Before accepting the header line, we need to see at least one character
                    // > so we can make sure there's no space or tab
                    var next = headersSpan[0];

                    if (next == ByteSpace || next == ByteTab)
                    {
                        // From https://tools.ietf.org/html/rfc7230#section-3.2.4:
                        //
                        // Historically, HTTP header field values could be extended over
                        // multiple lines by preceding each extra line with at least one space
                        // or horizontal tab (obs-fold).  This specification deprecates such
                        // line folding except within the message/http media type
                        // (Section 8.3.1).  A sender MUST NOT generate a message that includes
                        // line folding (i.e., that has any field-value that contains a match to
                        // the obs-fold rule) unless the message is intended for packaging
                        // within the message/http media type.
                        //
                        // A server that receives an obs-fold in a request message that is not
                        // within a message/http container MUST either reject the message by
                        // sending a 400 (Bad Request), preferably with a representation
                        // explaining that obsolete line folding is unacceptable, or replace
                        // each received obs-fold with one or more SP octets prior to
                        // interpreting the field value or forwarding the message downstream.
                        RejectRequest(RequestRejectionReason.HeaderValueLineFoldingNotSupported);
                    }

                    TakeSingleHeader(span, handler);

                    consumedBytes += span.Length;
                }
            }
            finally
            {
                consumed = buffer.Move(consumed, consumedBytes);
                if (completedHeaders)
                {
                    examined = consumed;
                }
            }
        }

        public unsafe void TakeSingleHeader<T>(Span<byte> span, T handler) where T : IHttpHeadersHandler
        {
            var state = HeaderState.Name;
            var nameStart = 0;
            var nameEnd = -1;
            var valueStart = -1;
            var valueEnd = -1;
            var nameHasWhitespace = false;
            var previouslyWhitespace = false;
            var headerLineLength = span.Length;

            fixed (byte* data = &span.DangerousGetPinnableReference())
            {
                for (var i = 0; i < headerLineLength; i++)
                {
                    var ch = data[i];

                    switch (state)
                    {
                        case HeaderState.Name:
                            if (ch == ByteColon)
                            {
                                if (nameHasWhitespace)
                                {
                                    RejectRequest(RequestRejectionReason.WhitespaceIsNotAllowedInHeaderName);
                                }

                                state = HeaderState.Whitespace;
                                nameEnd = i;
                            }

                            if (ch == ByteSpace || ch == ByteTab)
                            {
                                nameHasWhitespace = true;
                            }
                            break;
                        case HeaderState.Whitespace:
                            {
                                var whitespace = ch == ByteTab || ch == ByteSpace || ch == ByteCR;

                                if (!whitespace)
                                {
                                    // Mark the first non whitespace char as the start of the
                                    // header value and change the state to expect to the header value
                                    valueStart = i;
                                    state = HeaderState.ExpectValue;
                                }
                                // If we see a CR then jump to the next state directly
                                else if (ch == ByteCR)
                                {
                                    state = HeaderState.ExpectValue;
                                    goto case HeaderState.ExpectValue;
                                }
                            }
                            break;
                        case HeaderState.ExpectValue:
                            {
                                var whitespace = ch == ByteTab || ch == ByteSpace;

                                if (whitespace)
                                {
                                    if (!previouslyWhitespace)
                                    {
                                        // If we see a whitespace char then maybe it's end of the
                                        // header value
                                        valueEnd = i;
                                    }
                                }
                                else if (ch == ByteCR)
                                {
                                    // If we see a CR and we haven't ever seen whitespace then
                                    // this is the end of the header value
                                    if (valueEnd == -1)
                                    {
                                        valueEnd = i;
                                    }

                                    // We never saw a non whitespace character before the CR
                                    if (valueStart == -1)
                                    {
                                        valueStart = valueEnd;
                                    }

                                    state = HeaderState.ExpectNewLine;
                                }
                                else
                                {
                                    // If we find a non whitespace char that isn't CR then reset the end index
                                    valueEnd = -1;
                                }

                                previouslyWhitespace = whitespace;
                            }
                            break;
                        case HeaderState.ExpectNewLine:
                            if (ch != ByteLF)
                            {
                                RejectRequest(RequestRejectionReason.HeaderValueMustNotContainCR);
                            }

                            state = HeaderState.Complete;
                            break;
                        default:
                            break;
                    }
                }
            }

            if (state == HeaderState.Name)
            {
                RejectRequest(RequestRejectionReason.NoColonCharacterFoundInHeaderLine);
            }

            if (state == HeaderState.ExpectValue || state == HeaderState.Whitespace)
            {
                RejectRequest(RequestRejectionReason.MissingCRInHeaderLine);
            }

            var nameBuffer = span.Slice(nameStart, nameEnd - nameStart);
            var valueBuffer = span.Slice(valueStart, valueEnd - valueStart);

            handler.OnHeader(nameBuffer, valueBuffer);
        }

        public void Reset()
        {

        }

        private static bool IsValidTokenChar(char c)
        {
            // Determines if a character is valid as a 'token' as defined in the
            // HTTP spec: https://tools.ietf.org/html/rfc7230#section-3.2.6
            return
                (c >= '0' && c <= '9') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                c == '!' ||
                c == '#' ||
                c == '$' ||
                c == '%' ||
                c == '&' ||
                c == '\'' ||
                c == '*' ||
                c == '+' ||
                c == '-' ||
                c == '.' ||
                c == '^' ||
                c == '_' ||
                c == '`' ||
                c == '|' ||
                c == '~';
        }

        public void RejectRequest(RequestRejectionReason reason)
        {
            RejectRequest(BadHttpRequestException.GetException(reason));
        }

        public void RejectRequest(RequestRejectionReason reason, string value)
        {
            RejectRequest(BadHttpRequestException.GetException(reason, value));
        }

        private void RejectRequest(BadHttpRequestException ex)
        {
            throw ex;
        }

        private void RejectRequestLine(Span<byte> span)
        {
            const int MaxRequestLineError = 32;
            RejectRequest(RequestRejectionReason.InvalidRequestLine, span.GetAsciiStringEscaped(MaxRequestLineError));
        }

        private enum StartLineState
        {
            KnownMethod,
            UnknownMethod,
            Path,
            QueryString,
            KnownVersion,
            UnknownVersion,
            NewLine,
            Complete
        }

        private enum HeaderState
        {
            Name,
            Whitespace,
            ExpectValue,
            ExpectNewLine,
            Complete
        }
    }
}
