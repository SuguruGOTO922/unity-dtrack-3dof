/* ART DTRACK Plugin for Unity Game Engine: DTrackReceiverMarkers.cs
 *
 * Script providing DTRACK single 3DOF marker data to a fixed set of GameObjects.
 *
 * Local extension of the ART DTRACK Plugin (not part of the upstream release),
 * distributed under the same BSD-3-Clause license as this package.
 *
 * DTRACK assigns marker IDs per frame, so an ID changes whenever a marker is lost and
 * found again. This receiver therefore does not rely on IDs: each target ('slot') keeps
 * following the marker nearest to its previous position (greedy one-to-one matching).
 */

using System;
using System.Collections.Generic;
using UnityEngine;

using DTrackSDK;
using DTrack;
using DTrack.Util;

namespace DTrack
{


public class DTrackReceiverMarkers : DTrackReceiver
{
	[ Tooltip( "GameObjects following single markers (one marker per slot); entries may be empty to use the data from scripts only" ) ]
	public Transform[] targets = new Transform[ 0 ];

	[ Tooltip( "Update position of the target GameObjects" ) ]
	public bool applyPosition = true;

	[ Tooltip( "Maximum distance (in meter) a tracked marker may move between two DTRACK frames" ) ]
	public float maxJumpDistance = 0.05f;

	[ Tooltip( "Maximum distance (in meter) to pick up a new marker for an unassigned slot, measured from its last position (0 = unlimited)" ) ]
	public float acquireRadius = 0.0f;

	[ Tooltip( "Time (in seconds) after which a slot whose marker is lost becomes unassigned" ) ]
	public float lostTimeout = 0.5f;

	[ Tooltip( "Deactivate target GameObjects while their slot is unassigned" ) ]
	public bool hideWhenLost = false;


	// State of one slot (target).
	public class MarkerSlot
	{
		public bool IsAssigned  { get; internal set; }  // slot follows a marker (tracked, or lost for less than 'lostTimeout')
		public bool IsTracked  { get; internal set; }  // marker was found in the latest DTRACK frame
		public int DTrackId  { get; internal set; } = -1;  // marker ID in latest DTRACK frame (not persistent!)
		public Vector3 Position  { get; internal set; }  // last known position (Unity world coordinates)
		public float LastSeenTime  { get; internal set; } = float.NegativeInfinity;  // Time.time when marker was last found
	}

	private MarkerSlot[] slots = new MarkerSlot[ 0 ];

	private readonly List< Vector3 > allMarkerPositions = new List< Vector3 >();
	private readonly List< int > allMarkerIds = new List< int >();

	// Slots in the same order as 'targets'.
	public IReadOnlyList< MarkerSlot > Slots => this.slots;

	// Positions (Unity world coordinates) and DTRACK IDs of all tracked markers in the latest DTRACK frame.
	public IReadOnlyList< Vector3 > AllMarkerPositions => this.allMarkerPositions;
	public IReadOnlyList< int > AllMarkerIds => this.allMarkerIds;

	// Frame counter and timestamp of the latest processed DTRACK frame.
	public uint LastFrameCounter  { get; private set; }
	public double LastTimeStamp  { get; private set; }

	// Invoked (on the main thread) after a new DTRACK frame has been processed.
	public event Action< DTrackReceiverMarkers > MarkersUpdated;


	// Candidate pair of slot and marker for matching.
	private struct Candidate
	{
		public float distSq;
		public int slot;
		public int marker;
	}

	private readonly List< Candidate > candidates = new List< Candidate >();
	private bool[] isMarkerUsed = new bool[ 0 ];
	private bool[] isSlotMatched = new bool[ 0 ];


	void Start()
	{
		this.slots = new MarkerSlot[ this.targets.Length ];

		for ( int i = 0; i < this.targets.Length; i++ )
		{
			this.slots[ i ] = new MarkerSlot();

			Transform t = this.targets[ i ];
			if ( t == null )  continue;

			this.slots[ i ].Position = t.position;  // initial reference position to pick up a marker

			if ( this.transform.IsChildOf( t ) )
			{
				Debug.LogWarning( $"DTrackReceiverMarkers: target {i} contains this receiver; it won't be deactivated by 'hideWhenLost'" );
			}
		}

		this.isSlotMatched = new bool[ this.slots.Length ];

		updateVisibility();
	}

	void OnEnable()
	{
		this.Register();
	}

	void OnDisable()
	{
		this.Unregister();
	}


	void Update()
	{
		DTrackSDK.Frame frame = GetDTrackFrame();  // ensures data integrity against DTrack class
		if ( frame == null )  // no new tracking data
		{
			updateTimeouts();  // e.g. DTRACK stopped sending
			return;
		}

		try
		{
			processFrame( frame );
		}
		catch ( Exception e )
		{
			Debug.Log( $"Error while moving object: {e}" );
		}

		this.MarkersUpdated?.Invoke( this );
	}


	// Match markers of a new DTRACK frame to slots and move targets.

	private void processFrame( DTrackSDK.Frame frame )
	{
		this.LastFrameCounter = frame.FrameCounter;
		this.LastTimeStamp = frame.TimeStamp;

		this.allMarkerPositions.Clear();
		this.allMarkerIds.Clear();

		if ( frame.Markers != null )
		{
			foreach ( DTrackMarker m in frame.Markers.Values )
			{
				if ( ! m.IsTracked )  continue;

				this.allMarkerPositions.Add( Converter.PositionToUnity( m.Loc ) );
				this.allMarkerIds.Add( m.Id );
			}
		}

		int numMarkers = this.allMarkerPositions.Count;
		if ( this.isMarkerUsed.Length < numMarkers )  this.isMarkerUsed = new bool[ numMarkers ];
		Array.Clear( this.isMarkerUsed, 0, this.isMarkerUsed.Length );
		Array.Clear( this.isSlotMatched, 0, this.isSlotMatched.Length );

		float now = Time.time;

		// 1. assigned slots keep following the nearest marker within 'maxJumpDistance':
		matchSlots( true, this.maxJumpDistance, now );

		// 2. slots that didn't find their marker are lost; after 'lostTimeout' they become unassigned:
		for ( int i = 0; i < this.slots.Length; i++ )
		{
			if ( this.isSlotMatched[ i ] )  continue;

			MarkerSlot slot = this.slots[ i ];
			slot.IsTracked = false;
			slot.DTrackId = -1;

			if ( slot.IsAssigned && ( now - slot.LastSeenTime > this.lostTimeout ) )
				slot.IsAssigned = false;
		}

		// 3. unassigned slots pick up remaining markers:
		matchSlots( false, this.acquireRadius, now );

		// 4. move targets:
		if ( this.applyPosition )
		{
			for ( int i = 0; i < this.slots.Length; i++ )
			{
				if ( this.isSlotMatched[ i ] && ( this.targets[ i ] != null ) )
					this.targets[ i ].position = this.slots[ i ].Position;
			}
		}

		updateVisibility();
	}

	// Greedy one-to-one matching of (un)assigned slots to unused markers, nearest pairs first.

	private void matchSlots( bool assigned, float maxDistance, float now )
	{
		this.candidates.Clear();

		float maxDistSq = ( maxDistance > 0.0f ) ? maxDistance * maxDistance : float.PositiveInfinity;

		for ( int i = 0; i < this.slots.Length; i++ )
		{
			if ( this.isSlotMatched[ i ] || ( this.slots[ i ].IsAssigned != assigned ) )  continue;

			for ( int k = 0; k < this.allMarkerPositions.Count; k++ )
			{
				if ( this.isMarkerUsed[ k ] )  continue;

				float d = ( this.allMarkerPositions[ k ] - this.slots[ i ].Position ).sqrMagnitude;
				if ( d <= maxDistSq )
					this.candidates.Add( new Candidate { distSq = d, slot = i, marker = k } );
			}
		}

		this.candidates.Sort( ( a, b ) => a.distSq.CompareTo( b.distSq ) );

		foreach ( Candidate c in this.candidates )
		{
			if ( this.isSlotMatched[ c.slot ] || this.isMarkerUsed[ c.marker ] )  continue;

			this.isSlotMatched[ c.slot ] = true;
			this.isMarkerUsed[ c.marker ] = true;

			MarkerSlot slot = this.slots[ c.slot ];
			slot.IsAssigned = true;
			slot.IsTracked = true;
			slot.DTrackId = this.allMarkerIds[ c.marker ];
			slot.Position = this.allMarkerPositions[ c.marker ];
			slot.LastSeenTime = now;
		}
	}

	// Handle lost markers while no DTRACK frames arrive.

	private void updateTimeouts()
	{
		float now = Time.time;
		bool changed = false;

		foreach ( MarkerSlot slot in this.slots )
		{
			if ( slot.IsTracked && ( now - slot.LastSeenTime > this.lostTimeout ) )
			{
				slot.IsTracked = false;
				slot.DTrackId = -1;
				changed = true;
			}

			if ( slot.IsAssigned && ( now - slot.LastSeenTime > this.lostTimeout ) )
			{
				slot.IsAssigned = false;
				changed = true;
			}
		}

		if ( changed )  updateVisibility();
	}

	// Deactivate targets of unassigned slots, if requested.

	private void updateVisibility()
	{
		if ( ! this.hideWhenLost )  return;

		for ( int i = 0; i < this.slots.Length; i++ )
		{
			Transform t = this.targets[ i ];
			if ( ( t == null ) || this.transform.IsChildOf( t ) )  continue;

			bool active = this.slots[ i ].IsAssigned;
			if ( t.gameObject.activeSelf != active )  t.gameObject.SetActive( active );
		}
	}
}


}  // namespace DTrack
