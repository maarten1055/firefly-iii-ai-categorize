import {Mistral} from "@mistralai/mistralai";
import {getConfigVariable} from "./util.js";

export default class MistralService {
    #mistral;
    #model = "mistral-small-latest";

    constructor() {
        const apiKey = getConfigVariable("MISTRAL_API_KEY")
        this.#mistral = new Mistral({apiKey});
    }

    async classify(allLists, destinationName, description, recentTransactions = []) {
        try {
            const historyBlock = recentTransactions.length > 0
                ? `Recent categorized transactions for the same destination:
${recentTransactions.map((transaction, index) => `${index + 1}. date=${transaction.date}; amount=${transaction.amount} ${transaction.currencyCode ?? ""}; description=${transaction.description}; category=${transaction.category ?? "none"}; budget=${transaction.budget ?? "none"}`).join("\n")}

Use these as hints for consistency, but prefer the current transaction details if they clearly differ.

`
                : "";

            const prompt = `Categorize this transaction from my bank account with the following 
        description ${description} and the following destination ${destinationName}.
        ${historyBlock}
        Return the result by calling the classification function.
        If you cannot confidently classify it, still call the function and use "none" for unknown values.`;

            const categories = allLists.get('categories');
            const budgets = allLists.get('budgets');

            const request = {
                model: this.#model,
                messages: [{role: "user", content: prompt}],
                tools: [
                    {
                        "type": "function",
                        "function": {
                            "name": "classification",
                            "description": "Classify a financial transaction into a category and budget, use only values from the lists provided.",
                            "parameters": {
                                "type": "object",
                                "properties": {
                                    "category": {
                                        "type": "string",
                                        "description": `The category to classify the transaction into. 
                                    Use only these values: ${categories.join(", ")}.
                                    Use none if no category applies.`
                                    },
                                    "budget": {
                                        "type": "string",
                                        "description": `The budget to classify the transaction into.
                                    Use only these values: ${budgets.join(", ")}.
                                    Use none if no budget applies.
                                    `
                                    }
                                },
                                "required": ["category", "budget"]
                            }
                        }
                    }
                ],
                tool_choice: {
                    type: "function",
                    function: {name: "classification"}
                },
                parallel_tool_calls: false,
                temperature: 0,
            };

            const response = await this.#chat(request);
            const message = response.choices?.[0]?.message;
            const functionCall = message?.toolCalls?.[0] ?? message?.tool_calls?.[0];
            const rawArguments = functionCall?.function?.arguments ?? this.#extractJsonFromContent(message?.content);

            if (!rawArguments) {
                console.warn("Mistral response did not contain a tool call or parseable JSON content.", message);
                return null;
            }

            const json = JSON.parse(rawArguments);
            const category = categories.includes(json.category) ? json.category : null;
            const budget = budgets.includes(json.budget) ? json.budget : null;

            if (!category && !budget) {
                console.warn(`Mistral could not classify the transaction. 
                Prompt: ${prompt}
                Mistral's guess: ${rawArguments}`)
                return null;
            }

            return {
                prompt,
                response: rawArguments,
                category,
                budget
            }

        } catch (error) {
            if (error instanceof MistralException) {
                throw error;
            }

            const statusCode = error.statusCode ?? error.status ?? this.#extractStatusCode(error);
            const body = error.body ?? error.message;

            console.error(error.message);
            if (body && body !== error.message) {
                console.error(body);
            }

            throw new MistralException(statusCode, error.rawResponse ?? error.response ?? null, body);
        }
    }

    async diagnose() {
        const models = await this.#mistral.models.list();
        const availableModels = Array.isArray(models.data) ? models.data.map(model => model.id) : [];

        return {
            ok: true,
            model: this.#model,
            modelAvailable: availableModels.includes(this.#model),
            availableModelCount: availableModels.length
        };
    }

    async #chat(request) {
        return await this.#mistral.chat.complete(request);
    }

    #extractStatusCode(error) {
        const match = error?.message?.match(/status:\s*(\d{3})/i);
        return match ? Number(match[1]) : null;
    }

    #extractJsonFromContent(content) {
        if (!content) {
            return null;
        }

        const text = Array.isArray(content)
            ? content.map(part => typeof part === "string" ? part : part?.text ?? "").join("\n")
            : content;

        if (typeof text !== "string") {
            return null;
        }

        const trimmed = text.trim();
        if (trimmed.startsWith("{") && trimmed.endsWith("}")) {
            return trimmed;
        }

        const match = trimmed.match(/\{[\s\S]*\}/);
        return match ? match[0] : null;
    }
}

class MistralException extends Error {
    code;
    response;
    body;

    constructor(statusCode, response, body) {
        super(`Error while communicating with Mistral: ${statusCode} - ${body}`);

        this.code = statusCode;
        this.response = response;
        this.body = body;
    }
}
