import { ApiError, type Detail, type Me, type PurchaseRequest } from './api';

/** The values shown in the purchase dialog and sent on every retry. */
export interface PurchaseAttempt {
    item: Detail;
    characterId: number;
    characterName: string;
    count: number;
    price: number;
    balanceAfter: number;
    idempotencyKey: string;
    request: PurchaseRequest;
}

/** Freeze a listing, character, whole-stack price and idempotency key together. */
export function makePurchaseAttempt(item: Detail, account: Me, characterId: number, idempotencyKey: string = crypto.randomUUID()): PurchaseAttempt {
    const character = account.characters.find(candidate => candidate.id === characterId);
    if (!character) throw new ApiError('invalid_character', 400);
    const request: PurchaseRequest = {
        count: item.quantity,
        expectedPrice: item.price,
        characterId: character.id,
        idempotencyKey: idempotencyKey as PurchaseRequest['idempotencyKey'],
    };
    return {
        item,
        characterId: character.id,
        characterName: character.name,
        count: item.quantity,
        price: item.price,
        balanceAfter: account.balance - item.price,
        idempotencyKey,
        request,
    };
}
