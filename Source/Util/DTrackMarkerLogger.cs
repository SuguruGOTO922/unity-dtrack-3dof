/* ART DTRACK Plugin for Unity Game Engine: DTrackMarkerLogger.cs
 *
 * Script recording single 3DOF marker data of EVERY received DTRACK frame to CSV files.
 *
 * Local extension of the ART DTRACK Plugin (not part of the upstream release),
 * distributed under the same BSD-3-Clause license as this package.
 *
 * Frames are taken directly from the DTrack receive thread and written by a dedicated writer
 * thread, independent of Unity's frame rate or main thread stalls.
 *
 * Data file (one row per marker; one row with n_markers = 0 for frames without markers):
 *   recv_time_s,dtrack_frame,dtrack_ts,n_markers,marker_id,quality,x_mm,y_mm,z_mm,x,y,z
 *   recv_time_s : receive time (seconds since start of recording, monotonic high resolution clock)
 *   dtrack_ts   : DTRACK timestamp ('ts' output), -1 if not available
 *   x_mm..z_mm  : raw DTRACK position (mm, DTRACK room coordinates)
 *   x..z        : position in Unity world coordinates (meter)
 * Events file ('<data file>_events.csv'):
 *   recv_time_s,type,detail   with type = start | gap | counter_reset | no_frame_counter | receive_error | stop
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

using DTrackSDK;
using DTrack.Util;

using Debug = UnityEngine.Debug;

namespace DTrack
{


public class DTrackMarkerLogger : MonoBehaviour, IDTrackReceiver
{
	[ Tooltip( "CSV file path; empty: 'dtrack_markers_<date>_<time>.csv' in Application.persistentDataPath" ) ]
	public string filePath = "";

	[ Tooltip( "Interval (in seconds) to flush data to disk; limits data loss if the application crashes" ) ]
	public float flushIntervalSeconds = 1.0f;

	// Item passed from the receive thread to the writer thread.
	private struct Item
	{
		public long ticks;  // Stopwatch timestamp of receipt
		public Frame frame;  // received frame, or null for an error
		public string error;
	}

	private DTrack dtrackSource;
	private BlockingCollection< Item > queue;
	private Thread writerThread;
	private StreamWriter dataWriter;
	private StreamWriter eventsWriter;
	private long startTicks;

	private long framesWritten;
	private long gapCount;
	private long missingFrames;
	private long errorCount;

	// Paths of the files currently written (null if not recording).
	public string CurrentFilePath  { get; private set; }
	public string CurrentEventsFilePath  { get; private set; }

	// Statistics of the current recording (thread-safe).
	public long FramesWritten => Interlocked.Read( ref this.framesWritten );
	public long GapCount => Interlocked.Read( ref this.gapCount );
	public long MissingFrames => Interlocked.Read( ref this.missingFrames );  // sum of frame counter gaps
	public long ErrorCount => Interlocked.Read( ref this.errorCount );  // packets that failed to be received or parsed
	public int QueueLength => ( this.queue != null ) ? this.queue.Count : 0;  // frames waiting to be written


	void OnEnable()
	{
#if UNITY_2023_1_OR_NEWER
		this.dtrackSource = FindAnyObjectByType< DTrack >();
#else
		this.dtrackSource = FindObjectOfType< DTrack >();
#endif
		if ( this.dtrackSource == null )
		{
			Debug.LogError( "DTrackMarkerLogger: cannot find DTrack object, not recording" );
			return;
		}

		string path = String.IsNullOrEmpty( this.filePath )
				? Path.Combine( Application.persistentDataPath, $"dtrack_markers_{DateTime.Now:yyyyMMdd_HHmmss}.csv" )
				: this.filePath;
		string eventsPath = Path.Combine( Path.GetDirectoryName( Path.GetFullPath( path ) ),
		                                  Path.GetFileNameWithoutExtension( path ) + "_events.csv" );

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) ) );

			this.dataWriter = new StreamWriter( path, false, new UTF8Encoding( false ), 1 << 16 );
			this.eventsWriter = new StreamWriter( eventsPath, false, new UTF8Encoding( false ) );
		}
		catch ( Exception e )
		{
			Debug.LogError( $"DTrackMarkerLogger: cannot open {path}: {e.Message}" );
			closeFiles();
			return;
		}

		this.dataWriter.WriteLine( "recv_time_s,dtrack_frame,dtrack_ts,n_markers,marker_id,quality,x_mm,y_mm,z_mm,x,y,z" );
		this.eventsWriter.WriteLine( "recv_time_s,type,detail" );

		this.framesWritten = 0;
		this.gapCount = 0;
		this.missingFrames = 0;
		this.errorCount = 0;

		this.startTicks = Stopwatch.GetTimestamp();
		writeEvent( this.startTicks, "start",
		            $"time={DateTime.Now:o}; stopwatch_hz={Stopwatch.Frequency}; port={this.dtrackSource.listenPort}; " +
		            $"coordinates={this.dtrackSource.dTrackCoordinates}" );

		this.queue = new BlockingCollection< Item >();
		this.writerThread = new Thread( WriterThread ) { IsBackground = true, Name = "DTrackMarkerLogger" };
		this.writerThread.Start();

		this.CurrentFilePath = path;
		this.CurrentEventsFilePath = eventsPath;

		// start receiving only when everything is ready:
		this.dtrackSource.ReceiveError += this.OnReceiveError;
		this.dtrackSource.RegisterReceiver( this );

		Debug.Log( $"DTrackMarkerLogger: recording to {path}" );
	}

	void OnDisable()
	{
		if ( this.queue == null )  return;  // not recording

		if ( this.dtrackSource != null )
		{
			this.dtrackSource.UnregisterReceiver( this );
			this.dtrackSource.ReceiveError -= this.OnReceiveError;
		}

		this.queue.CompleteAdding();
		this.writerThread.Join();  // writes all queued frames before closing

		closeFiles();

		this.queue.Dispose();
		this.queue = null;
		this.writerThread = null;

		Debug.Log( $"DTrackMarkerLogger: stopped recording {this.CurrentFilePath} ({this.FramesWritten} frames, " +
		           $"{this.GapCount} gaps with {this.MissingFrames} missing frames, {this.ErrorCount} receive errors)" );

		this.CurrentFilePath = null;
		this.CurrentEventsFilePath = null;
	}


	// Receive new frame of DTRACK tracking data (called on the receive thread for every frame).

	public void ReceiveDTrackFrame( Frame frame )
	{
		enqueue( new Item { ticks = Stopwatch.GetTimestamp(), frame = frame } );
	}

	// Receive error of DTrack class (called on the receive thread).

	private void OnReceiveError( string message )
	{
		enqueue( new Item { ticks = Stopwatch.GetTimestamp(), error = message ?? "" } );
	}

	private void enqueue( Item item )
	{
		try
		{
			this.queue?.Add( item );
		}
		catch ( InvalidOperationException )  // recording just stopped
		{
		}
	}


	// Thread writing queued frames to disk.

	private void WriterThread()
	{
		long lastFrameCounter = -1;
		bool hasFrameCounter = true;
		long flushTicks = ( long )( Math.Max( 0.0f, this.flushIntervalSeconds ) * Stopwatch.Frequency );
		long lastFlush = Stopwatch.GetTimestamp();

		try
		{
			while ( ! this.queue.IsCompleted )
			{
				Item item;
				if ( this.queue.TryTake( out item, 100 ) )
				{
					if ( item.frame == null )
					{
						Interlocked.Increment( ref this.errorCount );
						writeEvent( item.ticks, "receive_error", item.error );
					}
					else
					{
						long fr = item.frame.FrameCounter;

						if ( lastFrameCounter >= 0 && hasFrameCounter )
						{
							if ( fr == 0 && lastFrameCounter == 0 )  // DTRACK output 'fr' not activated
							{
								hasFrameCounter = false;
								writeEvent( item.ticks, "no_frame_counter", "activate DTRACK output 'fr' to detect missing frames" );
							}
							else if ( fr - lastFrameCounter > 1 )
							{
								Interlocked.Increment( ref this.gapCount );
								Interlocked.Add( ref this.missingFrames, fr - lastFrameCounter - 1 );
								writeEvent( item.ticks, "gap",
								            $"after={lastFrameCounter}; next={fr}; missing={fr - lastFrameCounter - 1}" );
							}
							else if ( fr - lastFrameCounter <= 0 )
							{
								writeEvent( item.ticks, "counter_reset", $"after={lastFrameCounter}; next={fr}" );
							}
						}

						lastFrameCounter = fr;

						writeFrame( item );
						Interlocked.Increment( ref this.framesWritten );
					}
				}

				long now = Stopwatch.GetTimestamp();
				if ( now - lastFlush >= flushTicks )
				{
					this.dataWriter.Flush();
					this.eventsWriter.Flush();
					lastFlush = now;
				}
			}

			writeEvent( Stopwatch.GetTimestamp(), "stop",
			            $"frames={this.FramesWritten}; gaps={this.GapCount}; missing={this.MissingFrames}; errors={this.ErrorCount}" );
			this.dataWriter.Flush();
			this.eventsWriter.Flush();
		}
		catch ( Exception e )
		{
			Debug.LogError( $"DTrackMarkerLogger: writing failed, recording is incomplete: {e}" );
		}
	}

	private void writeFrame( Item item )
	{
		Frame frame = item.frame;
		int n = ( frame.Markers != null ) ? frame.Markers.Count : 0;

		string prefix = String.Format( CultureInfo.InvariantCulture, "{0:F6},{1},{2:F6},{3}",
		                               seconds( item.ticks ), frame.FrameCounter, frame.TimeStamp, n );

		if ( n == 0 )
		{
			this.dataWriter.WriteLine( prefix + ",,,,,,,," );
			return;
		}

		foreach ( DTrackMarker m in frame.Markers.Values )
		{
			Vector3 p = Converter.PositionToUnity( m.Loc );

			this.dataWriter.WriteLine( String.Format( CultureInfo.InvariantCulture,
			        "{0},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6:F6},{7:F6},{8:F6}",
			        prefix, m.Id, m.Quality, m.Loc[ 0 ], m.Loc[ 1 ], m.Loc[ 2 ], p.x, p.y, p.z ) );
		}
	}

	private void writeEvent( long ticks, string type, string detail )
	{
		this.eventsWriter.WriteLine( String.Format( CultureInfo.InvariantCulture, "{0:F6},{1},\"{2}\"",
		                                            seconds( ticks ), type, detail.Replace( "\"", "\"\"" ) ) );
	}

	private double seconds( long ticks )
	{
		return ( double )( ticks - this.startTicks ) / Stopwatch.Frequency;
	}

	private void closeFiles()
	{
		if ( this.dataWriter != null )  this.dataWriter.Close();
		if ( this.eventsWriter != null )  this.eventsWriter.Close();

		this.dataWriter = null;
		this.eventsWriter = null;
	}
}


}  // namespace DTrack
