import {getConfigVariable} from "./util.js";

export default class FireflyService {
    #BASE_URL;
    #PERSONAL_TOKEN;

    constructor() {
        this.#BASE_URL = getConfigVariable("FIREFLY_URL")
        if (this.#BASE_URL.slice(-1) === "/") {
            this.#BASE_URL = this.#BASE_URL.substring(0, this.#BASE_URL.length - 1)
        }

        this.#PERSONAL_TOKEN = getConfigVariable("FIREFLY_PERSONAL_TOKEN")
    }

    async getCategories() {
        const categories = new Map();

        for await (const category of this.#fetchCollection("/api/v1/categories")) {
            categories.set(category.attributes.name, category.id);
        }

        return categories;
    }

    async getBudgets() {
        const budgets = new Map();

        for await (const budget of this.#fetchCollection("/api/v1/budgets")) {
            budgets.set(budget.attributes.name, budget.id);
        }

        return budgets;
    }

    async getBills()    {
        const response = await fetch(`${this.#BASE_URL}/api/v1/bills`, {
            headers: {
                Authorization: `Bearer ${this.#PERSONAL_TOKEN}`,
            }
        });

        if (!response.ok) {
            throw new FireflyException(response.status, response, await response.text())
        }

        const data = await response.json();

        const bills = new Map();
        data.data.forEach(bill => {
            bills.set(bill.attributes.name, bill.id);
        });

        return bills;
    }

    async setCategoryAndBudget(transactionId, transactions, categoryId, budgetId) {
        const tag = getConfigVariable("FIREFLY_TAG", "AI categorized");

        const body = {
            apply_rules: true,
            fire_webhooks: true,
            transactions: [],
        }

        transactions.forEach(transaction => {
            const tags = Array.from(new Set([...(transaction.tags ?? []), tag]));

            const object = {
                transaction_journal_id: transaction.transaction_journal_id,
                tags: tags,
            }

            if (categoryId !== -1) {
                object.category_id = categoryId;
            }

            if (budgetId !== -1) {
                object.budget_id = budgetId;
            }

            body.transactions.push(object);
        })

        const response = await fetch(`${this.#BASE_URL}/api/v1/transactions/${transactionId}`, {
            method: "PUT",
            headers: {
                Authorization: `Bearer ${this.#PERSONAL_TOKEN}`,
                "Content-Type": "application/json",
            },
            body: JSON.stringify(body)
        });

        if (!response.ok) {
            throw new FireflyException(response.status, response, await response.text())
        }

        await response.json();
        console.info("Transaction updated")
    }

    async *#fetchCollection(path) {
        let page = 1;

        while (true) {
            const response = await fetch(`${this.#BASE_URL}${path}?page=${page}`, {
                headers: {
                    Authorization: `Bearer ${this.#PERSONAL_TOKEN}`,
                }
            });

            if (!response.ok) {
                throw new FireflyException(response.status, response, await response.text())
            }

            const data = await response.json();
            const items = data.data ?? [];

            for (const item of items) {
                yield item;
            }

            if (items.length === 0 || !data.meta?.pagination || page >= data.meta.pagination.total_pages) {
                break;
            }

            page += 1;
        }
    }
}

class FireflyException extends Error {
    code;
    response;
    body;

    constructor(statusCode, response, body) {
        super(`Error while communicating with Firefly III: ${statusCode} - ${body}`);

        this.code = statusCode;
        this.response = response;
        this.body = body;
    }
}
