/* DTrackSDK in C#: DTrackMarker.cs
 *
 * Data object containing DTRACK output data of one single 3DOF marker.
 *
 * Local extension of the ART DTRACK Plugin (not part of the upstream release),
 * distributed under the same BSD-3-Clause license as this package.
 *
 * Note: DTRACK assigns marker IDs per frame; an ID is NOT persistent and may change
 * whenever a marker is lost and found again.
 */

using DTrackSDK.Interfaces;

namespace DTrackSDK
{


public class DTrackMarker : DTrackObject, IDTrackLocation
{
	public float[] Loc  { get; }


	public DTrackMarker( int id, float quality, float sx, float sy, float sz )
			: base( id, quality )
	{
		this.Loc = new float[ 3 ];
		this.Loc[ 0 ] = sx;
		this.Loc[ 1 ] = sy;
		this.Loc[ 2 ] = sz;
	}
}


}  // namespace DTrackSDK
