/* ART DTRACK Plugin for Unity Game Engine: DTrackMarkerLogger.cs
 *
 * Script recording single 3DOF marker data of EVERY received DTRACK frame to CSV files.
 *
 * Copyright (c) 2026 Suguru Goto
 *
 * Local extension of the ART DTRACK Plugin (not part of the upstream release),
 * distributed under the same BSD-3-Clause license as this package.
 *
 * Frames are taken directly from the DTrack receive thread and written by a dedicated writer
 * thread, independent of Unity's frame rate or main thread stalls.
 *
 * Recording is controlled from scripts: StartRecording() creates a new file each time,
 * StopRecording() writes all received frames and closes the files (also done on disable/destroy).
 * Existing files are never overwritten: if '<name>.csv' or '<name>_events.csv' exists,
 * '<name>_1.csv', '<name>_2.csv', ... are used instead (see FilePath).
 *
 * Data file (one row per marker; one row with n_markers = 0 for frames without markers):
 *   recv_time_s,dtrack_frame,dtrack_ts,n_markers,marker_id,quality,x_mm,y_mm,z_mm,x,y,z
 *   recv_time_s : receive time (seconds since start of recording, monotonic high resolution clock)
 *   dtrack_ts   : DTRACK timestamp ('ts' output), -1 if not available
 *   x_mm..z_mm  : raw DTRACK position (mm, DTRACK room coordinates)
 *   x..z        : position in Unity world coordinates (meter)
 * Events file ('<data file>_events.csv'):
 *   recv_time_s,type,detail   with type = start | gap | counter_reset | no_frame_counter | receive_error |
 *                             no_data | data_resumed | stop
 *
 * Missing data: StartRecording() fails if the DTrack object is disabled or could not open its UDP port;
 * while recording, 'no_data' is reported if no frame arrives for 'noDataTimeoutSeconds' (DataReceivingChanged).
 * Write failures (e.g. disk full): recording stops automatically, RecordingFailed is invoked, LastError is set;
 * frames received after the failure are dropped (DroppedFrames) instead of filling memory.
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

	[ Tooltip( "Report 'no data' if no DTRACK frame arrives for this time (in seconds)" ) ]
	public float noDataTimeoutSeconds = 2.0f;

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
	private int listenPort;

	private long lastReceiveTicks;  // Stopwatch timestamp of the latest frame received (receive thread)
	private volatile bool noData;  // no frame for 'noDataTimeoutSeconds' (writer thread)
	private int noDataPeriods;
	private bool notifiedReceivingData;  // state last reported on the main thread

	private volatile bool writeFailed;  // writing failed, recording is incomplete
	private volatile string lastError;
	private long droppedFrames;

	private const int MaxFileNumber = 9999;  // highest number appended to file names

	private long framesWritten;
	private long gapCount;
	private long missingFrames;
	private long errorCount;

	// Paths of the files of the current or most recent recording (null before the first recording).
	public string FilePath  { get; private set; }
	public string EventsFilePath  { get; private set; }

	// True between a successful StartRecording() and StopRecording().
	public bool IsRecording => ( this.queue != null );

	// Statistics of the current or most recent recording (thread-safe).
	public long FramesWritten => Interlocked.Read( ref this.framesWritten );
	public long GapCount => Interlocked.Read( ref this.gapCount );
	public long MissingFrames => Interlocked.Read( ref this.missingFrames );  // sum of frame counter gaps
	public long ErrorCount => Interlocked.Read( ref this.errorCount );  // packets that failed to be received or parsed
	public int QueueLength => ( this.queue != null ) ? this.queue.Count : 0;  // frames waiting to be written

	// False while recording and no frame arrived for 'noDataTimeoutSeconds'.
	public bool IsReceivingData => ! ( this.IsRecording && this.noData );
	public double SecondsSinceLastFrame =>
			( double )( Stopwatch.GetTimestamp() - Interlocked.Read( ref this.lastReceiveTicks ) ) / Stopwatch.Frequency;

	// Invoked on the main thread when data stop (false) or resume (true) arriving while recording.
	public event Action< DTrackMarkerLogger, bool > DataReceivingChanged;

	// Write failure of the current or most recent recording (Failed, LastError) and frames dropped after it.
	public bool Failed => this.writeFailed;
	public string LastError => this.lastError;
	public long DroppedFrames => Interlocked.Read( ref this.droppedFrames );

	// Invoked on the main thread (with LastError) after a write failure stopped the recording.
	public event Action< DTrackMarkerLogger, string > RecordingFailed;


	// Start recording to a new file (call on the main thread).
	//
	// path: CSV file path; null or empty: 'filePath', or 'dtrack_markers_<date>_<time>.csv' in Application.persistentDataPath
	// Returns false if already recording or if recording could not be started.

	public bool StartRecording( string path = null )
	{
		if ( this.IsRecording )
		{
			Debug.LogWarning( $"DTrackMarkerLogger: already recording to {this.FilePath}" );
			return false;
		}

#if UNITY_2023_1_OR_NEWER
		this.dtrackSource = FindAnyObjectByType< DTrack >();
#else
		this.dtrackSource = FindObjectOfType< DTrack >();
#endif
		if ( this.dtrackSource == null )
		{
			Debug.LogError( "DTrackMarkerLogger: cannot find DTrack object, not recording" );
			return false;
		}

		if ( ! this.dtrackSource.isActiveAndEnabled )
		{
			Debug.LogError( "DTrackMarkerLogger: DTrack object is disabled, not recording" );
			return false;
		}

		if ( this.dtrackSource.IsStarted && ! this.dtrackSource.IsReceiving )
		{
			Debug.LogError( $"DTrackMarkerLogger: DTrack could not open UDP port {this.dtrackSource.listenPort} " +
			                "(used by another application?), not recording" );
			return false;
		}

		if ( String.IsNullOrEmpty( path ) )  path = this.filePath;
		if ( String.IsNullOrEmpty( path ) )
			path = Path.Combine( Application.persistentDataPath, $"dtrack_markers_{DateTime.Now:yyyyMMdd_HHmmss}.csv" );

		string requestedPath = Path.GetFullPath( path );
		string eventsPath;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( requestedPath ) );

			path = openNewFiles( requestedPath, out eventsPath );
		}
		catch ( Exception e )
		{
			Debug.LogError( $"DTrackMarkerLogger: cannot open {requestedPath}: {e.Message}" );
			closeFiles();
			return false;
		}

		this.dataWriter.WriteLine( "recv_time_s,dtrack_frame,dtrack_ts,n_markers,marker_id,quality,x_mm,y_mm,z_mm,x,y,z" );
		this.eventsWriter.WriteLine( "recv_time_s,type,detail" );

		this.framesWritten = 0;
		this.gapCount = 0;
		this.missingFrames = 0;
		this.errorCount = 0;

		this.startTicks = Stopwatch.GetTimestamp();
		this.listenPort = this.dtrackSource.listenPort;
		this.lastReceiveTicks = this.startTicks;
		this.noData = false;
		this.noDataPeriods = 0;
		this.notifiedReceivingData = true;
		this.writeFailed = false;
		this.lastError = null;
		this.droppedFrames = 0;
		writeEvent( this.startTicks, "start",
		            $"time={DateTime.Now:o}; stopwatch_hz={Stopwatch.Frequency}; port={this.dtrackSource.listenPort}; " +
		            $"coordinates={this.dtrackSource.dTrackCoordinates}" +
		            ( ( path != requestedPath ) ? $"; requested_path={requestedPath}" : "" ) );

		this.queue = new BlockingCollection< Item >();
		this.writerThread = new Thread( WriterThread ) { IsBackground = true, Name = "DTrackMarkerLogger" };
		this.writerThread.Start();

		this.FilePath = path;
		this.EventsFilePath = eventsPath;

		// start receiving only when everything is ready:
		this.dtrackSource.ReceiveError += this.OnReceiveError;
		this.dtrackSource.RegisterReceiver( this );

		Debug.Log( $"DTrackMarkerLogger: recording to {path}" );
		return true;
	}

	// Stop recording (call on the main thread); writes all frames received so far and closes the files.
	//
	// Does nothing if not recording. Called automatically when this component is disabled or destroyed.

	public void StopRecording()
	{
		if ( ! this.IsRecording )  return;

		try
		{
			if ( this.dtrackSource != null )
			{
				this.dtrackSource.UnregisterReceiver( this );
				this.dtrackSource.ReceiveError -= this.OnReceiveError;
			}

			this.queue.CompleteAdding();
			this.writerThread.Join();  // writes all queued frames before closing (returns at once if writing failed)
		}
		finally  // always leave a clean, stopped state
		{
			closeFiles();

			this.queue.Dispose();
			this.queue = null;
			this.writerThread = null;
		}

		if ( this.writeFailed )
		{
			Debug.LogError( $"DTrackMarkerLogger: recording {this.FilePath} is INCOMPLETE: {this.lastError} " +
			                $"({this.FramesWritten} frames written, {this.DroppedFrames} frames dropped after the failure)" );
			return;
		}

		Debug.Log( $"DTrackMarkerLogger: stopped recording {this.FilePath} ({this.FramesWritten} frames, " +
		           $"{this.GapCount} gaps with {this.MissingFrames} missing frames, {this.ErrorCount} receive errors, " +
		           $"{this.noDataPeriods} periods without data)" );

		if ( this.FramesWritten == 0 )
			Debug.LogError( $"DTrackMarkerLogger: recording {this.FilePath} is EMPTY, no DTRACK frame was received" );
	}

	// Report changes of the 'no data' state on the main thread.

	void Update()
	{
		if ( ! this.IsRecording )  return;

		if ( this.writeFailed )  // stop and report on the main thread
		{
			StopRecording();
			this.RecordingFailed?.Invoke( this, this.lastError );
			return;
		}

		bool receiving = ! this.noData;
		if ( receiving == this.notifiedReceivingData )  return;

		this.notifiedReceivingData = receiving;

		if ( receiving )
			Debug.Log( "DTrackMarkerLogger: DTRACK data resumed" );
		else
			Debug.LogWarning( $"DTrackMarkerLogger: no DTRACK data for {this.noDataTimeoutSeconds} s " +
			                  $"(check DTrack component, UDP port {this.listenPort} and DTRACK output settings)" );

		this.DataReceivingChanged?.Invoke( this, receiving );
	}

	void OnDisable()
	{
		StopRecording();
	}

	void OnDestroy()
	{
		StopRecording();
	}


	// Receive new frame of DTRACK tracking data (called on the receive thread for every frame).

	public void ReceiveDTrackFrame( Frame frame )
	{
		long ticks = Stopwatch.GetTimestamp();
		Interlocked.Exchange( ref this.lastReceiveTicks, ticks );

		enqueue( new Item { ticks = ticks, frame = frame } );
	}

	// Receive error of DTrack class (called on the receive thread).

	private void OnReceiveError( string message )
	{
		enqueue( new Item { ticks = Stopwatch.GetTimestamp(), error = message ?? "" } );
	}

	private void enqueue( Item item )
	{
		if ( this.writeFailed )  // writer thread is gone: don't fill memory
		{
			if ( item.frame != null )  Interlocked.Increment( ref this.droppedFrames );
			return;
		}

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
		long noDataTicks = ( long )( Math.Max( 0.1f, this.noDataTimeoutSeconds ) * Stopwatch.Frequency );
		long lastFrameTicks = this.startTicks;  // receive time of the previous frame written

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
						if ( item.ticks - lastFrameTicks > noDataTicks )  // first frame after a period without data
						{
							if ( ! this.noData )  // not yet reported (e.g. writer was busy)
							{
								this.noDataPeriods++;
								writeEvent( lastFrameTicks + noDataTicks, "no_data", noDataDetail() );
							}

							writeEvent( item.ticks, "data_resumed", String.Format( CultureInfo.InvariantCulture,
							            "after {0:F1} s without frames", ( double )( item.ticks - lastFrameTicks ) / Stopwatch.Frequency ) );
							this.noData = false;
						}

						lastFrameTicks = item.ticks;

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

				// receive side timestamp: correct even if the writer lags behind
				if ( ! this.noData && ( now - Interlocked.Read( ref this.lastReceiveTicks ) > noDataTicks ) )
				{
					this.noDataPeriods++;
					writeEvent( now, "no_data", noDataDetail() );
					this.noData = true;
				}

				if ( now - lastFlush >= flushTicks )
				{
					this.dataWriter.Flush();
					this.eventsWriter.Flush();
					lastFlush = now;
				}
			}

			writeEvent( Stopwatch.GetTimestamp(), "stop",
			            $"frames={this.FramesWritten}; gaps={this.GapCount}; missing={this.MissingFrames}; errors={this.ErrorCount}; " +
			            $"no_data_periods={this.noDataPeriods}" + ( ( this.FramesWritten == 0 ) ? "; empty=true" : "" ) );
			this.dataWriter.Flush();
			this.eventsWriter.Flush();
		}
		catch ( Exception e )
		{
			this.lastError = $"writing failed: {e.Message}";
			this.writeFailed = true;

			try  // record the failure if the events file is still writable
			{
				writeEvent( Stopwatch.GetTimestamp(), "write_error", e.Message );
				this.eventsWriter.Flush();
			}
			catch ( Exception )
			{
			}
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

	private string noDataDetail()
	{
		return String.Format( CultureInfo.InvariantCulture, "no frame for {0:F1} s; check DTrack component, UDP port {1} and DTRACK output",
		                      this.noDataTimeoutSeconds, this.listenPort );
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

	// Create data and events files that don't exist yet: '<name><ext>', else '<name>_1<ext>', '<name>_2<ext>', ...
	// FileMode.CreateNew is atomic, so existing files are never overwritten, even by concurrent recordings.

	private string openNewFiles( string requestedPath, out string eventsPath )
	{
		string dir = Path.GetDirectoryName( requestedPath );
		string name = Path.GetFileNameWithoutExtension( requestedPath );
		string ext = Path.GetExtension( requestedPath );

		for ( int n = 0; n <= MaxFileNumber; n++ )
		{
			string stem = ( n == 0 ) ? name : $"{name}_{n}";
			string path = Path.Combine( dir, stem + ext );
			eventsPath = Path.Combine( dir, stem + "_events.csv" );

			FileStream data = tryCreateNew( path );
			if ( data == null )  continue;  // exists

			FileStream events = tryCreateNew( eventsPath );
			if ( events == null )  // exists: discard the empty data file just created, try next number
			{
				data.Dispose();
				File.Delete( path );
				continue;
			}

			this.dataWriter = new StreamWriter( data, new UTF8Encoding( false ), 1 << 16 );
			this.eventsWriter = new StreamWriter( events, new UTF8Encoding( false ) );
			return path;
		}

		throw new IOException( $"no free file name up to '{name}_{MaxFileNumber}{ext}'" );
	}

	// Create a new file; null if a file or directory of this name exists already.

	private static FileStream tryCreateNew( string path )
	{
		try
		{
			return new FileStream( path, FileMode.CreateNew, FileAccess.Write, FileShare.Read );
		}
		catch ( IOException ) when ( File.Exists( path ) || Directory.Exists( path ) )
		{
			return null;
		}
	}

	private void closeFiles()
	{
		closeFile( this.dataWriter );
		closeFile( this.eventsWriter );

		this.dataWriter = null;
		this.eventsWriter = null;
	}

	// Close a file; a failure (e.g. writing the last buffered data) marks the recording as failed.

	private void closeFile( StreamWriter writer )
	{
		if ( writer == null )  return;

		try
		{
			writer.Close();
		}
		catch ( Exception e )
		{
			if ( ! this.writeFailed )  this.lastError = $"closing file failed: {e.Message}";
			this.writeFailed = true;

			try  // release the file handle without writing
			{
				writer.BaseStream.Dispose();
			}
			catch ( Exception )
			{
			}
		}
	}
}


}  // namespace DTrack
