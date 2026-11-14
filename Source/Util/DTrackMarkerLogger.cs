/* ART DTRACK Plugin for Unity Game Engine: DTrackMarkerLogger.cs
 *
 * Script recording single 3DOF marker data of a DTrackReceiverMarkers to a CSV file.
 *
 * Local extension of the ART DTRACK Plugin (not part of the upstream release),
 * distributed under the same BSD-3-Clause license as this package.
 *
 * One row per slot and processed DTRACK frame; positions in Unity world coordinates (meter):
 *   unity_time,dtrack_frame,dtrack_timestamp,slot,dtrack_id,tracked,x,y,z
 * With 'logAllMarkers', every tracked marker of the frame is recorded additionally with slot = -1.
 */

using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DTrack
{


[ RequireComponent( typeof( DTrackReceiverMarkers ) ) ]
public class DTrackMarkerLogger : MonoBehaviour
{
	[ Tooltip( "CSV file path; empty: 'dtrack_markers_<date>_<time>.csv' in Application.persistentDataPath" ) ]
	public string filePath = "";

	[ Tooltip( "Also record all tracked markers of each frame (slot = -1)" ) ]
	public bool logAllMarkers = false;

	private DTrackReceiverMarkers receiver;
	private StreamWriter writer;

	// Path of the file currently written (null if not recording).
	public string CurrentFilePath  { get; private set; }


	void Awake()
	{
		this.receiver = GetComponent< DTrackReceiverMarkers >();
	}

	void OnEnable()
	{
		string path = String.IsNullOrEmpty( this.filePath )
				? Path.Combine( Application.persistentDataPath, $"dtrack_markers_{DateTime.Now:yyyyMMdd_HHmmss}.csv" )
				: this.filePath;

		try
		{
			string dir = Path.GetDirectoryName( path );
			if ( ! String.IsNullOrEmpty( dir ) )  Directory.CreateDirectory( dir );

			this.writer = new StreamWriter( path, false );
			this.writer.WriteLine( "unity_time,dtrack_frame,dtrack_timestamp,slot,dtrack_id,tracked,x,y,z" );
			this.CurrentFilePath = path;
			Debug.Log( $"DTrackMarkerLogger: recording to {path}" );
		}
		catch ( Exception e )
		{
			Debug.LogError( $"DTrackMarkerLogger: cannot open {path}: {e.Message}" );
			this.writer = null;
			return;
		}

		this.receiver.MarkersUpdated += this.OnMarkersUpdated;
	}

	void OnDisable()
	{
		this.receiver.MarkersUpdated -= this.OnMarkersUpdated;

		if ( this.writer != null )
		{
			this.writer.Close();
			this.writer = null;
		}

		this.CurrentFilePath = null;
	}


	private void OnMarkersUpdated( DTrackReceiverMarkers rec )
	{
		if ( this.writer == null )  return;

		string prefix = String.Format( CultureInfo.InvariantCulture, "{0:F4},{1},{2:F6}",
		                               Time.time, rec.LastFrameCounter, rec.LastTimeStamp );

		for ( int i = 0; i < rec.Slots.Count; i++ )
		{
			DTrackReceiverMarkers.MarkerSlot slot = rec.Slots[ i ];
			writeRow( prefix, i, slot.DTrackId, slot.IsTracked, slot.Position );
		}

		if ( this.logAllMarkers )
		{
			for ( int k = 0; k < rec.AllMarkerPositions.Count; k++ )
			{
				writeRow( prefix, -1, rec.AllMarkerIds[ k ], true, rec.AllMarkerPositions[ k ] );
			}
		}
	}

	private void writeRow( string prefix, int slot, int id, bool tracked, Vector3 p )
	{
		this.writer.WriteLine( String.Format( CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:F5},{5:F5},{6:F5}",
		                                      prefix, slot, id, tracked ? 1 : 0, p.x, p.y, p.z ) );
	}
}


}  // namespace DTrack
