using System.Threading.Tasks;

namespace BARS_Client_V2.Domain;

public interface IPointStateListener
{
    void OnPointStateChanged(PointState state);
}
