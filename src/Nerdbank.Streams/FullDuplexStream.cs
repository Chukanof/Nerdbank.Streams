﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;

    /// <summary>
    /// Provides a full duplex stream which may be shared by two parties to
    /// exchange messages.
    /// </summary>
    public static class FullDuplexStream
    {
        /// <summary>
        /// Creates a pair of streams that can be passed to two parties
        /// to allow for interaction with each other.
        /// </summary>
        /// <returns>A pair of streams.</returns>
        public static Tuple<Stream, Stream> CreatePair()
        {
            var stream1 = new PairedStream();
            var stream2 = new PairedStream();
            stream1.SetOtherStream(stream2);
            stream2.SetOtherStream(stream1);
            return Tuple.Create<Stream, Stream>(stream1, stream2);
        }

        /// <summary>
        /// Combines a readable <see cref="Stream"/> with a writable <see cref="Stream"/> into a new full-duplex <see cref="Stream"/>
        /// that reads and writes to the specified streams.
        /// </summary>
        /// <param name="readableStream">A readable stream.</param>
        /// <param name="writableStream">A writable stream.</param>
        /// <returns>A new full-duplex stream.</returns>
        public static Stream Splice(Stream readableStream, Stream writableStream) => new CombinedStream(readableStream, writableStream);

        private class CombinedStream : Stream, IDisposableObservable
        {
            private readonly Stream readableStream;
            private readonly Stream writableStream;

            internal CombinedStream(Stream readableStream, Stream writableStream)
            {
                Requires.NotNull(readableStream, nameof(readableStream));
                Requires.NotNull(writableStream, nameof(writableStream));

                Requires.Argument(readableStream.CanRead, nameof(readableStream), "Must be readable");
                Requires.Argument(writableStream.CanWrite, nameof(writableStream), "Must be writable");

                this.readableStream = readableStream;
                this.writableStream = writableStream;
            }

            public override bool CanRead => !this.IsDisposed;

            public override bool CanSeek => false;

            public override bool CanWrite => !this.IsDisposed;

            public override bool CanTimeout => this.readableStream.CanTimeout || this.writableStream.CanTimeout;

            public override int ReadTimeout
            {
                get => this.readableStream.ReadTimeout;
                set => this.readableStream.ReadTimeout = value;
            }

            public override int WriteTimeout
            {
                get => this.writableStream.WriteTimeout;
                set => this.writableStream.WriteTimeout = value;
            }

            public override long Length => throw this.ThrowDisposedOr(new NotSupportedException());

            public override long Position
            {
                get => throw this.ThrowDisposedOr(new NotSupportedException());
                set => throw this.ThrowDisposedOr(new NotSupportedException());
            }

            public bool IsDisposed { get; private set; }

            public override long Seek(long offset, SeekOrigin origin) => throw this.ThrowDisposedOr(new NotSupportedException());

            public override void SetLength(long value) => this.ThrowDisposedOr(new NotSupportedException());

            public override void Flush() => this.writableStream.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => this.writableStream.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => this.readableStream.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.readableStream.ReadAsync(buffer, offset, count, cancellationToken);

            public override int ReadByte() => this.readableStream.ReadByte();

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => this.readableStream.CopyToAsync(destination, bufferSize, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count) => this.writableStream.Write(buffer, offset, count);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.writableStream.WriteAsync(buffer, offset, count, cancellationToken);

            public override void WriteByte(byte value) => this.writableStream.WriteByte(value);

#if NETCOREAPP2_1

            public override void Write(ReadOnlySpan<byte> buffer) => this.writableStream.Write(buffer);

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => this.writableStream.WriteAsync(buffer, cancellationToken);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => this.readableStream.ReadAsync(buffer, cancellationToken);
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix

            public override int Read(Span<byte> buffer) => this.readableStream.Read(buffer);

#endif

            protected override void Dispose(bool disposing)
            {
                this.IsDisposed = true;
                if (disposing)
                {
                    this.readableStream.Dispose();
                    this.writableStream.Dispose();
                }

                base.Dispose(disposing);
            }

            private Exception ThrowDisposedOr(Exception ex)
            {
                Verify.NotDisposed(this);
                throw ex;
            }
        }

        /// <summary>
        /// The full duplex stream.
        /// </summary>
        private class PairedStream : Stream, IDisposableObservable
        {
            /// <summary>
            /// The options to use when creating the value for <see cref="enqueuedSource"/>.
            /// </summary>
            private const TaskCreationOptions EnqueuedSourceOptions = TaskCreationOptions.None;
            private static readonly byte[] EmptyByteArray = new byte[0];
            private static readonly Task CompletedTask = Task.FromResult<object>(null);

            /// <summary>
            /// The messages posted by the <see cref="other"/> party,
            /// for this stream to read.
            /// </summary>
            private readonly List<Message> readQueue = new List<Message>();

            /// <summary>
            /// The completion source for a Task that completes whenever a message
            /// is enqueued to <see cref="readQueue"/>.
            /// </summary>
            private TaskCompletionSource<object> enqueuedSource = new TaskCompletionSource<object>(EnqueuedSourceOptions);

            /// <summary>
            /// The stream to write to.
            /// </summary>
            private PairedStream other;

            /// <inheritdoc />
            public bool IsDisposed { get; private set; }

            /// <inheritdoc />
            public override bool CanRead => !this.IsDisposed;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => !this.IsDisposed;

            /// <inheritdoc />
            public override long Length
            {
                get => throw this.ThrowDisposedOr(new NotSupportedException());
            }

            /// <inheritdoc />
            public override long Position
            {
                get => throw this.ThrowDisposedOr(new NotSupportedException());
                set => throw this.ThrowDisposedOr(new NotSupportedException());
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Requires.NotNull(buffer, nameof(buffer));
                Requires.Range(offset >= 0, nameof(offset));
                Requires.Range(count >= 0, nameof(count));
                Requires.Range(offset + count <= buffer.Length, nameof(count));
                Verify.NotDisposed(this);

                cancellationToken.ThrowIfCancellationRequested();
                Message message = null;
                while (message == null)
                {
                    Task waitTask = null;
                    lock (this.readQueue)
                    {
                        if (this.readQueue.Count > 0)
                        {
                            message = this.readQueue[0];
                        }
                        else
                        {
                            waitTask = this.enqueuedSource.Task;
                        }
                    }

                    if (waitTask != null)
                    {
                        if (cancellationToken.CanBeCanceled)
                        {
                            // Arrange to wake up when a new message is posted, or when the caller's CancellationToken is canceled.
                            var wakeUpEarly = new TaskCompletionSource<object>();
                            using (cancellationToken.Register(state => ((TaskCompletionSource<object>)state).SetResult(null), wakeUpEarly, false))
                            {
                                await Task.WhenAny(waitTask, wakeUpEarly.Task).ConfigureAwait(false);
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            // The caller didn't pass in a CancellationToken. So just do the cheapest thing.
                            await waitTask.ConfigureAwait(false);
                        }
                    }
                }

                int copiedBytes = message.Consume(buffer, offset, count);
                if (message.IsConsumed)
                {
                    lock (this.readQueue)
                    {
                        Assumes.True(this.readQueue[0] == message); // if this fails, the caller is calling Read[Async] in a non-sequential way.
                        this.readQueue.RemoveAt(0);
                    }
                }

                return copiedBytes;
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count)
            {
                Requires.NotNull(buffer, nameof(buffer));
                Requires.Range(offset >= 0, nameof(offset));
                Requires.Range(count >= 0, nameof(count));
                Requires.Range(offset + count <= buffer.Length, nameof(count));
                Verify.NotDisposed(this);

                lock (this.readQueue)
                {
                    while (this.readQueue.Count == 0)
                    {
                        Monitor.Wait(this.readQueue);
                    }

                    var message = this.readQueue[0];
                    int copiedBytes = message.Consume(buffer, offset, count);
                    if (message.IsConsumed)
                    {
                        this.readQueue.RemoveAt(0);
                    }

                    // Note that the message we just read may not have fully filled
                    // our caller's available space in the buffer. But that's OK.
                    // MSDN Stream documentation allows us to return less.
                    // But we should not return 0 bytes back unless the sender has
                    // closed their stream.
                    return copiedBytes;
                }
            }

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count)
            {
                Requires.NotNull(buffer, nameof(buffer));
                Requires.Range(offset >= 0, nameof(offset));
                Requires.Range(count >= 0, nameof(count));
                Requires.Range(offset + count <= buffer.Length, nameof(count));
                Verify.NotDisposed(this);

                // Avoid sending an empty buffer because that is the signal of a closed stream.
                if (count > 0)
                {
                    byte[] queuedBuffer = new byte[count];
                    Array.Copy(buffer, offset, queuedBuffer, 0, count);
                    this.other.PostMessage(new Message(queuedBuffer));
                }
            }

            /// <inheritdoc />
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Verify.NotDisposed(this);
                cancellationToken.ThrowIfCancellationRequested();
                this.Write(buffer, offset, count);
                return CompletedTask;
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin)
            {
                Verify.NotDisposed(this);
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void SetLength(long value)
            {
                Verify.NotDisposed(this);
                throw new NotSupportedException();
            }

            /// <summary>
            /// Sets the stream to copy written data to.
            /// </summary>
            /// <param name="other">The other stream.</param>
            internal void SetOtherStream(PairedStream other)
            {
                Requires.NotNull(other, nameof(other));
                Assumes.Null(this.other);
                this.other = other;
            }

            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                // Sending an empty buffer is the traditional way to signal
                // that the transmitting stream has closed.
                this.other.PostMessage(new Message(EmptyByteArray));
                this.IsDisposed = true;
                base.Dispose(disposing);
            }

            /// <summary>
            /// Posts a message to this stream's read queue.
            /// </summary>
            /// <param name="message">The message to transmit.</param>
            private void PostMessage(Message message)
            {
                Requires.NotNull(message, nameof(message));

                TaskCompletionSource<object> enqueuedSource;
                lock (this.readQueue)
                {
                    this.readQueue.Add(message);
                    Monitor.PulseAll(this.readQueue);
                    enqueuedSource = Interlocked.Exchange(ref this.enqueuedSource, new TaskCompletionSource<object>(EnqueuedSourceOptions));
                }

                enqueuedSource.TrySetResult(null);
            }

            /// <summary>
            /// Throws <see cref="ObjectDisposedException"/> if the object is disposed,
            /// otherwise throws the given exception.
            /// </summary>
            /// <param name="ex">The new exception to throw.</param>
            /// <returns>No value is ever returned. This method always throws.</returns>
            private Exception ThrowDisposedOr(Exception ex)
            {
                Verify.NotDisposed(this);
                throw ex;
            }
        }

        private class Message
        {
            internal Message(byte[] buffer)
            {
                Requires.NotNull(buffer, nameof(buffer));

                this.Buffer = buffer;
            }

            /// <summary>
            /// Gets a value indicating whether this message has been read completely
            /// and should be removed from the queue.
            /// </summary>
            /// <remarks>
            /// This returns <c>false</c> if the buffer was originally empty,
            /// since that signifies that the other party closed their sending stream.
            /// </remarks>
            public bool IsConsumed => this.Position == this.Buffer.Length && this.Buffer.Length > 0;

            /// <summary>
            /// Gets the buffer to read from.
            /// </summary>
            private byte[] Buffer { get; }

            /// <summary>
            /// Gets or sets the position within the buffer that indicates the first
            /// character that has not yet been read.
            /// </summary>
            private int Position { get; set; }

            public int Consume(byte[] buffer, int offset, int count)
            {
                int copiedBytes = Math.Min(count, this.Buffer.Length - this.Position);
                Array.Copy(this.Buffer, this.Position, buffer, offset, copiedBytes);
                this.Position += copiedBytes;
                Assumes.False(this.Position > this.Buffer.Length);
                return copiedBytes;
            }
        }
    }
}
