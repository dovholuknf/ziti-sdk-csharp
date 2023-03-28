#if NET6_0_OR_GREATER
namespace src.OpenZiti {
    public enum ZitiRouterStatus {
        EdgeRouterAdded = 0,
        EdgeRouterConnected,
        EdgeRouterDisconnected,
        EdgeRouterRemoved,
        EdgeRouterUnavailable,
    }
}

#endif
