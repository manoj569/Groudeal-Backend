using GroupBuy.Domain;

namespace GroupBuy.Application;
public record Actor(Guid UserId, bool IsAdmin, string CorrelationId);
public interface IIdentityService
{
    Task<UserResponse> Register(RegisterRequest request, CancellationToken ct);
    Task<TokenResponse> Login(LoginRequest request, CancellationToken ct);
    Task<UserResponse> Me(Guid userId, CancellationToken ct);
    Task Logout(Guid userId, CancellationToken ct);
}
public interface IMarketplace
{
    Task<PageResponse<CategoryResponse>> Categories(PageRequest page, CancellationToken ct);
    Task<CategoryResponse> Category(string slug, CancellationToken ct);
    Task<PageResponse<SellerResponse>> Sellers(PageRequest page, CancellationToken ct);
    Task<SellerResponse> Seller(Guid id, CancellationToken ct);
    Task<SellerResponse> SaveSeller(Actor actor, SellerRequest request, bool create, CancellationToken ct);
    Task<SellerPrivateProfileResponse> SellerProfile(Actor actor, CancellationToken ct);
    Task<PageResponse<SellerProductResponse>> SellerProducts(Actor actor, PageRequest page, ResourceStatus? status, CancellationToken ct);
    Task<SellerProductResponse> SetProductStatus(Actor actor, Guid id, ProductStatusRequest request, CancellationToken ct);
    Task<DashboardResponse> Dashboard(Actor actor, CancellationToken ct);
    Task<PageResponse<ProductResponse>> Products(PageRequest page, CancellationToken ct);
    Task<ProductResponse> Product(Guid id, CancellationToken ct);
    Task<ProductResponse> SaveProduct(Actor actor, Guid? id, ProductRequest request, CancellationToken ct);
    Task<PageResponse<CampaignResponse>> Campaigns(PageRequest page, CancellationToken ct);
    Task<CampaignResponse> Campaign(Guid id, CancellationToken ct);
    Task<CampaignResponse> SaveCampaign(Actor actor, Guid? id, CampaignRequest request, CancellationToken ct);
    Task<CampaignResponse> Submit(Actor actor, Guid id, CancellationToken ct);
    Task<CampaignResponse> Transition(Actor actor, Guid id, TransitionRequest request, CancellationToken ct);
    Task<ReservationResponse> Reserve(Actor actor, Guid id, ReservationRequest request, CancellationToken ct);
    Task<PageResponse<ReservationResponse>> Reservations(Actor actor, PageRequest page, CancellationToken ct);
    Task<ReservationResponse> Cancel(Actor actor, Guid id, CancellationToken ct);
    Task<PageResponse<SellerReviewResponse>> PendingSellers(Actor actor, PageRequest page, CancellationToken ct);
    Task<SellerReviewResponse> ReviewSeller(Actor actor, Guid id, bool approve, ReviewRequest request, CancellationToken ct);
    Task<PageResponse<CampaignResponse>> PendingCampaigns(Actor actor, PageRequest page, CancellationToken ct);
    Task<CampaignResponse> ReviewCampaign(Actor actor, Guid id, bool approve, ReviewRequest request, CancellationToken ct);
}
public interface ICampaignExpiryProcessor { Task Process(CancellationToken ct); }
