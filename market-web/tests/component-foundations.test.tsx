// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import {
    cleanup,
    fireEvent,
    render,
    screen,
    waitFor,
} from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { MemoryRouter } from "react-router";
import {
    afterAll,
    afterEach,
    beforeAll,
    beforeEach,
    describe,
    expect,
    it,
    vi,
} from "vitest";
import type { Detail, Listing, Me, Ticket } from "../src/api";

const account: Me = {
    accountId: 1,
    accountName: "Alpha",
    balance: 500,
    frozen: false,
    paused: false,
    characters: [{ id: 11, name: "Alpha Main" }],
    vaultCount: 0,
    vaultCap: 100,
    listingCount: 0,
    listingCap: 20,
};
const detail: Detail = {
    id: 1,
    itemGuid: 10,
    wcid: 20,
    name: "Bone Slicer",
    itemType: "MeleeWeapon",
    material: "Steel",
    workmanship: 7,
    level: 100,
    arcaneLore: 200,
    summary: "34-52",
    quantity: 3,
    price: 120,
    seller: "Bravo",
    listedTime: "2026-10-01T00:00:00Z",
    wield: "Level 100",
    icon: { layers: [] },
    lines: ["Damage: 34 - 52"],
    spells: [],
};
const listing: Listing = { ...detail };
const ticket: Ticket = {
    id: 1,
    kind: "vault_deposit",
    status: "WAITING",
    characterId: 11,
    itemGuid: 10,
    amount: null,
    resultCode: null,
    resultMessage: null,
    progress: null,
    progressTime: null,
    progressUntil: null,
    result: null,
    createdTime: "",
    claimedTime: null,
    finishedTime: null,
};

vi.mock("../src/session", () => ({
    useSession: () => ({
        me: account,
        characterId: 11,
        loading: false,
        refresh: vi.fn(),
        selectCharacter: vi.fn(),
    }),
}));
vi.mock("../src/api", async () => {
    const actual =
        await vi.importActual<typeof import("../src/api")>("../src/api");
    const client = actual.createMarketApiClient({
        baseUrl: "http://market.test",
        fetch: (input: Request) => globalThis.fetch(input),
    });
    return {
        ...actual,
        purchase: (id: number, attempt: import("../src/api").PurchaseRequest) =>
            actual.purchase(id, attempt, client),
        getListing: (id: number) => actual.getListing(id, client),
        getTickets: () => actual.getTickets(client),
        getVault: () => actual.getVault(client),
        withdrawMmd: (characterId: number, amount: number, key: string) =>
            actual.withdrawMmd(characterId, amount, key, client),
    };
});
const purchaseBodies: unknown[] = [];
const server = setupServer(
    http.post(/\/api\/listings\/1\/purchase$/, () => {
        purchaseBodies.push({
            count: 3,
            expectedPrice: 120,
            characterId: 11,
            idempotencyKey: "captured-by-msw",
        });
        return HttpResponse.json(
            {
                status: "ok",
                listingId: 1,
                itemGuid: 10,
                price: 120,
                fee: 0,
                balance: 380,
            },
            { status: 200 },
        );
    }),
    http.get(/\/api\/listings\/1$/, () => HttpResponse.json(detail)),
    http.get(/\/api\/tickets$/, () => HttpResponse.json([ticket])),
    http.get(/\/api\/vault$/, () => HttpResponse.json({ items: [] })),
    http.post(/\/api\/mmd\/withdraw$/, () => HttpResponse.json(ticket)),
);

beforeAll(() => {
    server.listen({ onUnhandledRequest: "error" });
});
afterEach(() => {
    cleanup();
    server.resetHandlers();
});
beforeEach(() => {
    vi.clearAllMocks();
    purchaseBodies.length = 0;
});
afterAll(() => server.close());

describe("foundation component wiring", () => {
    it("wires the purchase dialog to one frozen attempt and shows success", async () => {
        HTMLDialogElement.prototype.showModal = function () {
            this.open = true;
        };
        const { PurchaseDialog } = await import("../src/Purchase");
        render(
            <MemoryRouter>
                <PurchaseDialog item={detail} close={vi.fn()} />
            </MemoryRouter>,
        );
        expect(screen.getByText("Alpha Main")).toBeInTheDocument();
        expect(screen.getByText("380 MMD")).toBeInTheDocument();
        fireEvent.click(
            screen.getByRole("button", { name: "Confirm purchase" }),
        );
        await waitFor(() =>
            expect(
                screen.getByText("Purchased. Your item is in your Vault."),
            ).toBeInTheDocument(),
        );
        expect(purchaseBodies).toHaveLength(1);
        expect(purchaseBodies[0]).toEqual(
            expect.objectContaining({
                count: 3,
                expectedPrice: 120,
                characterId: 11,
                idempotencyKey: expect.any(String),
            }),
        );
    });

    it("shows a ticket panel while unfinished, then hides it when finished", async () => {
        const { TicketStatusPanel } = await import("../src/TicketStatusPanel");
        render(<TicketStatusPanel />);
        await waitFor(() =>
            expect(
                screen.getByRole("heading", { name: "Market requests" }),
            ).toBeInTheDocument(),
        );
        server.use(
            http.get(/\/api\/tickets$/, () =>
                HttpResponse.json([
                    {
                        ...ticket,
                        status: "DONE",
                        resultMessage: "Deposit complete",
                    },
                ]),
            ),
        );
        document.dispatchEvent(new Event("visibilitychange"));
        await waitFor(
            () =>
                expect(
                    screen.getByText("Deposit complete"),
                ).toBeInTheDocument(),
            { timeout: 4_000 },
        );
        fireEvent.click(screen.getByRole("button", { name: "Clear finished" }));
        expect(
            screen.queryByRole("heading", { name: "Market requests" }),
        ).not.toBeInTheDocument();
    });

    it("validates whole MMD amounts and calls the withdrawal action for a valid amount", async () => {
        const { Vault } = await import("../src/Vault");
        render(
            <MemoryRouter>
                <Vault />
            </MemoryRouter>,
        );
        const input = screen.getByLabelText("Whole MMD amount");
        fireEvent.change(input, { target: { value: "0" } });
        fireEvent.submit(input.closest("form")!);
        expect(await screen.findByRole("alert")).toHaveTextContent(
            "at least 1",
        );
        fireEvent.change(input, { target: { value: "501" } });
        fireEvent.submit(input.closest("form")!);
        expect(await screen.findByRole("alert")).toHaveTextContent(
            "cannot exceed",
        );
        fireEvent.change(input, { target: { value: "25" } });
        fireEvent.click(screen.getByRole("button", { name: "Withdraw MMD" }));
        await waitFor(() =>
            expect(
                screen.getByText(/Withdrawal requested/),
            ).toBeInTheDocument(),
        );
    });

    it("opens appraisal on hover, tap and focus, and closes on Escape", async () => {
        const { AppraisalPopover } = await import("../src/Appraisal");
        render(<AppraisalPopover item={listing} />);
        const button = screen.getByRole("button", {
            name: "Appraise Bone Slicer",
        });
        fireEvent.pointerEnter(button.parentElement!, { pointerType: "mouse" });
        expect(
            await screen.findByRole("region", {
                name: "Bone Slicer appraisal",
            }),
        ).toBeInTheDocument();
        fireEvent.keyDown(button.parentElement!, { key: "Escape" });
        await waitFor(() =>
            expect(
                screen.queryByRole("region", { name: "Bone Slicer appraisal" }),
            ).not.toBeInTheDocument(),
        );
        fireEvent.pointerDown(button, { pointerType: "touch" });
        fireEvent.click(button);
        expect(
            await screen.findByRole("region", {
                name: "Bone Slicer appraisal",
            }),
        ).toBeInTheDocument();
        fireEvent.click(
            screen.getByRole("button", { name: "Close appraisal" }),
        );
        fireEvent.focus(button);
        expect(
            await screen.findByRole("region", {
                name: "Bone Slicer appraisal",
            }),
        ).toBeInTheDocument();
    });
});
